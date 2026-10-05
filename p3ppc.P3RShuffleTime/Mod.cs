using p3ppc.P3RShuffleTime.Configuration;
using p3ppc.P3RShuffleTime.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Hooks.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using System.Runtime.InteropServices;

namespace p3ppc.P3RShuffleTime;

public unsafe class Mod : ModBase
{
    private readonly IModLoader _modLoader;
    private readonly Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks _hooks;
    private Config _configuration;

    private IAsmHook? _forceFreeSelectModePatch;
    private IAsmHook? _skipFlipAndShufflePatch;
    private IAsmHook? _hideShuffleCountdownPatch;
    private IAsmHook? _keepShuffleTimerWaitingPatch;
    private IReverseWrapper<ResetShuffleRewardDelegate>? _resetShuffleRewardReverseWrapper;
    private IHook<UpdateShufflePersonaResultDelegate>? _updateShufflePersonaResultHook;
    private IAsmHook? _swordSkillCardItemPatch;
    private IReverseWrapper<SelectSwordSkillCardDelegate>? _selectSwordSkillCardReverseWrapper;

    private ItemTblEntry** _itemTblEntries;

    private SkillCardRewardTable _skillCardRewards = new();
    private string _skillCardRewardPath = string.Empty;

    private const int SkillCardItemBase = 0x690;
    private const int SupportedSkillCardCount = 0x100;
    private const int SkillCardDescriptionTable = 6;

    private GetNumPersonasDelegate? _getNumPersonas;
    private GetProtagPersonaDelegate? _getProtagPersona;
    private GetPersonaArcanaDelegate? _getPersonaArcana;
    private GetPersonaRequiredExpDelegate? _getPersonaRequiredExp;

    private bool _rewardProcessedThisShuffle;


    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _configuration = context.Configuration;

        if (!Utils.Initialise(context.Logger, _configuration, _modLoader))
        {
            throw new InvalidOperationException("Failed to initialise P3R Shuffle Time utilities.");
        }

        var hooksController = _modLoader.GetController<Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks>();
        if (hooksController == null || !hooksController.TryGetTarget(out _hooks!))
        {
            throw new InvalidOperationException("Unable to get Reloaded Hooks controller.");
        }

        if (_configuration.EnableSwordSkillCards)
        {
            LoadSkillCardRewards();
        }

        Utils.LogDebug("Registering Shuffle Time hooks.");
        RegisterFunctions();

        Utils.LogDebug("Initialised; waiting for scans.");
    }

    private void RegisterFunctions()
    {
        if (_configuration.EnableSelectableCards)
        {
            Utils.SigScan("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 48 8B F9 41 8B D9 48 8D 0D ?? ?? ?? ?? 41 8B F0 48 8B EA", "Force Shuffle Time Free Select Mode", address =>
            {
                string resetCall = _hooks.Utilities.GetAbsoluteCallMnemonics(ResetShuffleRewardState, out _resetShuffleRewardReverseWrapper);
                string[] asm =
                {
                    "use64",
                    "push rax",
                    "push rcx",
                    "push rdx",
                    "push r8",
                    "sub rsp, 0x28",
                    resetCall,
                    "add rsp, 0x28",
                    "pop r8",
                    "pop rdx",
                    "pop rcx",
                    "pop rax",
                    "mov r9d, 3"
                };
                
                _forceFreeSelectModePatch = _hooks.CreateAsmHook(asm, address, AsmHookBehaviour.ExecuteFirst).Activate();
                Utils.LogDebug("Free-select patch enabled.");
            });

            Utils.SigScan("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 81 EC 90 00 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F9", "Skip Shuffle Time Flip And Shuffle", address =>
            {
                string[] asm =
                {
                    "use64",
                    "cmp dword [rcx + 0xB280], 3",
                    "jne noShuffleDone",
                    "mov dword [rcx + 0xB280], 7",
                    "mov dword [rcx + 0xAF6C], 0",
                    "label noShuffleDone"
                };
                
                _skipFlipAndShufflePatch = _hooks.CreateAsmHook(asm, address, AsmHookBehaviour.ExecuteFirst).Activate();
                Utils.LogDebug("Flip-and-shuffle skip enabled.");
            });

            Utils.SigScan("E8 ?? ?? ?? ?? 66 83 8F ?? ?? ?? ?? 10 48 8B 8F ?? ?? ?? ??", "Shuffle countdown call", address =>
            {
                _hideShuffleCountdownPatch = _hooks.CreateAsmHook(new[] { "use64" }, address, AsmHookBehaviour.DoNotExecuteOriginal).Activate();
                Utils.LogDebug("Countdown patch enabled.");
            });

            Utils.SigScan("40 53 48 83 EC 20 48 89 CB 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 43 ?? B9 00 04 00 00", "Keep Shuffle Time Timer Waiting", address =>
            {
                string[] asm =
                {
                    "use64",
                    "mov rax, [rcx + 0x48]",
                    "test rax, rax",
                    "jz timerWaitDone",
                    "or word [rax + 0x70], 0x400",
                    "and byte [rax + 0x08], 0xFD",
                    "label timerWaitDone"
                };
                
                _keepShuffleTimerWaitingPatch = _hooks.CreateAsmHook(asm, address, AsmHookBehaviour.ExecuteFirst).Activate();
                Utils.LogDebug("Timer wait patch enabled.");
            });

        }
        else
        {
            Utils.LogDebug("Selectable cards are disabled.");
        }

        Utils.SigScan("40 53 56 57 48 83 EC 40 48 89 CB", "UpdateShufflePersonaResult", address =>
        {
            _updateShufflePersonaResultHook = _hooks.CreateHook<UpdateShufflePersonaResultDelegate>(UpdateShufflePersonaResult, address).Activate();
            Utils.LogDebug("Duplicate Persona hook enabled.");
        });

        if (_configuration.EnableSwordSkillCards)
        {

            Utils.SigScan("48 89 05 ?? ?? ?? ?? 44 89 C2", "Skill Card Item Table", address =>
            {
                _itemTblEntries = (ItemTblEntry**)Utils.GetGlobalAddress(address + 3);
                Utils.LogDebug("Skill Card validation enabled.");
            });


            Utils.SigScan("45 0F B7 2C CE 44 8B 75 7F 8B 7D 77 4C 8D 05 ?? ?? ?? ??", "Sword Reward Item ID", address =>
            {
                string selectCall = _hooks.Utilities.GetAbsoluteCallMnemonics(SelectSwordSkillCardItem, out _selectSwordSkillCardReverseWrapper);
                
                string[] asm =
                {
                    "use64",
                    "mov rax, [rbp + 0x67]",
                    "cmp word [rax + 0x18], 0",
                    "jne keepVanillaSwordReward",
                    "movzx ecx, byte [rax + 0x20]",
                    "movzx edx, r13w",
                    "sub rsp, 0x20",
                    selectCall,
                    "add rsp, 0x20",
                    "cmp eax, 0x690",
                    "jb keepVanillaSwordReward",
                    "cmp eax, 0x790",
                    "jae keepVanillaSwordReward",
                    "movzx r13d, ax",
                    "label keepVanillaSwordReward"
                };
                
                _swordSkillCardItemPatch = _hooks.CreateAsmHook(asm, address, AsmHookBehaviour.ExecuteAfter).Activate();
                Utils.LogDebug("Sword Skill Card patch enabled.");
            });
        }
        else
        {
            Utils.LogDebug("Sword Skill Cards are disabled.");
        }

        Utils.SigScan("40 53 48 83 EC 20 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 31 DB E8 ?? ?? ?? ??", "GetNumPersonas", address =>
        {
            _getNumPersonas = _hooks.CreateWrapper<GetNumPersonasDelegate>(address, out _);
        });

        Utils.SigScan("E8 ?? ?? ?? ?? 0F B6 C8 E8 ?? ?? ?? ?? 48 63 C8", "GetPersonaArcana", address =>
        {
            _getPersonaArcana = _hooks.CreateWrapper<GetPersonaArcanaDelegate>(Utils.ResolveRelativeCall(address), out _);
        });

        Utils.SigScan("40 53 48 83 EC 20 0F B7 D9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 66 85 DB 78 ?? E8 ?? ?? ?? ?? 0F B7 D0 0F BF C3 39 D0 7C ?? 8B 15 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? FF C2 E8 ?? ?? ?? ?? 48 0F BF C3 48 6B C8 34 48 8D 05 ?? ?? ?? ?? 48 01 C8", "GetProtagPersona", address =>
        {
            _getProtagPersona = _hooks.CreateWrapper<GetProtagPersonaDelegate>(address, out _);
        });

        Utils.SigScan("E8 ?? ?? ?? ?? BB C0 00 00 00 41 3B C7", "GetPersonaRequiredExp", address =>
        {
            _getPersonaRequiredExp = _hooks.CreateWrapper<GetPersonaRequiredExpDelegate>(Utils.ResolveRelativeCall(address), out _);
        });

    }

    private void LoadSkillCardRewards()
    {
        string modDirectory = Path.GetDirectoryName(typeof(Mod).Assembly.Location) ?? AppContext.BaseDirectory;
        _skillCardRewardPath = Path.Combine(modDirectory, "skill_cards.json");

        try
        {
            _skillCardRewards = SkillCardRewardTable.Load(_skillCardRewardPath);
            Utils.LogDebug($"Loaded Sword skill-card table from {_skillCardRewardPath}.");
        }
        catch (Exception ex)
        {
            _skillCardRewards = new SkillCardRewardTable();
            Utils.LogError("Could not load skill_cards.json; using vanilla Sword rewards.", ex);
        }
    }

    private int SelectSwordSkillCardItem(int rank, int vanillaItemId)
    {
        try
        {
            rank = Math.Clamp(rank, 1, 10);

            SkillCardRewardEntry? reward = _skillCardRewards.Pick(rank, SupportedSkillCardCount);
            if (reward == null)
            {
                Utils.LogError($"Sword rank {rank} has no valid Skill Card; the vanilla reward is kept.");
                return 0;
            }

            if (_itemTblEntries == null || *_itemTblEntries == null)
            {
                Utils.LogError("ITEM.TBL is not ready, so the game Sword reward was kept.");
                return 0;
            }

            int index = reward.SkillCardIndex;
            short itemId = checked((short)(SkillCardItemBase + index));
            ItemTblEntry entry = (*_itemTblEntries)[itemId];

            if (entry.DescriptionTable != SkillCardDescriptionTable || entry.Skill == 0)
            {
                Utils.LogError($"Sword rank {rank} gave an invalid Skill Card, so the game reward was kept.");
                return 0;
            }

            string name = string.IsNullOrWhiteSpace(reward.Name) ? $"Skill Card 0x{(ushort)itemId:X3}" : reward.Name;

            Utils.LogDebug($"Sword rank {rank}: {name}, item=0x{(ushort)itemId:X3}.");

            return itemId;
        }
        catch (Exception ex)
        {
            Utils.LogError("Skill Card selection failed, so the game Sword reward was kept", ex);
            return 0;
        }
    }

    private void ResetShuffleRewardState()
    {
        _rewardProcessedThisShuffle = false;
        Utils.LogDebug("Shuffle Time reward state reset.");
    }

    private uint UpdateShufflePersonaResult(nint resultContext)
    {
        if (_updateShufflePersonaResultHook == null)
        {
            return 0;
        }

        try
        {
            int state = *(int*)(resultContext + 0x10);
            short selectedPersonaId = *(short*)(resultContext + 0x18);

            if (state == 0 && !_rewardProcessedThisShuffle)
            {
                _rewardProcessedThisShuffle = true;
                Utils.LogDebug($"Persona reward: state=0, persona={selectedPersonaId}.");

                if (!_configuration.EnableDuplicatePersonaArcanaExp)
                {
                    Utils.LogDebug("Duplicate Persona EXP is off.");
                }
                else if (!AreRewardFunctionsReady())
                {
                    Utils.LogError($"Some duplicate reward hooks are not ready: {GetMissingRewardFunctions()}.");
                }
                else
                {
                    Persona* duplicate = FindOwnedPersona(selectedPersonaId);

                    if (duplicate != null)
                    {
                        byte arcana = _getPersonaArcana!((ushort)selectedPersonaId);
                        Utils.LogDebug($"Duplicate Persona {selectedPersonaId} uses {GetArcanaName(arcana)}.");

                        Utils.LogDebug("Applying duplicate Persona EXP.");
                        ApplyDuplicateArcanaReward(selectedPersonaId, arcana);
                        Utils.LogDebug("Duplicate Persona EXP applied.");
                    }
                    else
                    {
                        Utils.LogDebug($"Persona {selectedPersonaId} is not in stock, so the game reward was kept.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Utils.LogError("Duplicate Persona reward failed", ex);
        }

        return _updateShufflePersonaResultHook.OriginalFunction(resultContext);
    }

    private Persona* FindOwnedPersona(short personaId)
    {
        ushort count = _getNumPersonas!();
        Utils.LogDebug($"Duplicate check: persona={personaId}, stock={count}.");

        for (short slot = 0; slot < count; slot++)
        {
            Persona* persona = _getProtagPersona!(slot);
            if (persona == null)
            {
                Utils.LogDebug($"Persona stock slot {slot}: null.");
                continue;
            }

            Utils.LogDebug($"Persona slot {slot}: id={persona->Id}, level={persona->Level}, exp={persona->Exp}.");

            if (persona->Id == personaId)
            {
                return persona;
            }
        }

        return null;
    }

    private bool AreRewardFunctionsReady()
    {
        return _getNumPersonas != null && _getProtagPersona != null && _getPersonaArcana != null && _getPersonaRequiredExp != null;
    }

    private string GetMissingRewardFunctions()
    {
        List<string> missing = new();

        if (_getNumPersonas == null)
        {
            missing.Add(nameof(_getNumPersonas));
        }
        if (_getProtagPersona == null)
        {
            missing.Add(nameof(_getProtagPersona));
        }
        if (_getPersonaArcana == null)
        {
            missing.Add(nameof(_getPersonaArcana));
        }
        if (_getPersonaRequiredExp == null)
        {
            missing.Add(nameof(_getPersonaRequiredExp));
        }

        return missing.Count == 0 ? "none" : string.Join(", ", missing);
    }

    private void ApplyDuplicateArcanaReward(short selectedPersonaId, byte selectedArcana)
    {
        ushort count = _getNumPersonas!();
        int affected = 0;

        for (short slot = 0; slot < count; slot++)
        {
            Persona* persona = _getProtagPersona!(slot);

            if (persona == null || persona->Id <= 0 || persona->Level >= 99)
            {
                continue;
            }

            if (!_configuration.IncludeSelectedPersona && persona->Id == selectedPersonaId)
            {
                continue;
            }

            byte arcana = _getPersonaArcana!((ushort)persona->Id);
            if (arcana != selectedArcana)
            {
                continue;
            }

            int exp = CalculateRewardExp(persona);
            if (exp <= 0)
            {
                continue;
            }

            int beforeExp = persona->Exp;
            persona->Exp += exp;
            affected++;

            Utils.LogDebug($"Arcana EXP: slot={slot}, Persona={persona->Id}, bonus={exp}.");
        }

        Utils.LogDebug($"Duplicate Persona {selectedPersonaId} gave EXP to {affected} Persona(s).");
    }

    private int CalculateRewardExp(Persona* persona)
    {
        if (persona == null || persona->Level >= 99 || _getPersonaRequiredExp == null)
        {
            return 0;
        }

        int requiredExp = _getPersonaRequiredExp(persona, (ushort)(persona->Level + 1));
        int remainingExp = requiredExp - persona->Exp;

        if (remainingExp <= 1)
        {
            return 0;
        }

        double percent = 0.15 + (Random.Shared.NextDouble() * 0.10);
        int reward = (int)Math.Floor(remainingExp * percent);
        reward = Math.Clamp(reward, 0, remainingExp - 1);

        Utils.LogDebug($"Arcana EXP: persona={persona->Id}, reward={reward}, rate={percent * 100.0:F1}%.");

        return reward;
    }

    private static string GetArcanaName(byte arcana)
    {
        return arcana switch
        {
            1 => "Fool",
            2 => "Magician",
            3 => "Priestess",
            4 => "Empress",
            5 => "Emperor",
            6 => "Hierophant",
            7 => "Lovers",
            8 => "Chariot",
            9 => "Justice",
            10 => "Hermit",
            11 => "Fortune",
            12 => "Strength",
            13 => "Hanged Man",
            14 => "Death",
            15 => "Temperance",
            16 => "Devil",
            17 => "Tower",
            18 => "Star",
            19 => "Moon",
            20 => "Sun",
            21 => "Judgement",
            22 => "Aeon",
            _ => "Unknown"
        };
    }

#pragma warning disable CS8618
    public Mod() { }
#pragma warning restore CS8618

    public override void ConfigurationUpdated(Config configuration)
    {
        _configuration = configuration;
        Utils.UpdateConfig(configuration);
        Utils.LogDebug("Configuration updated.");
    }

    private delegate void ResetShuffleRewardDelegate();
    private delegate uint UpdateShufflePersonaResultDelegate(nint resultContext);

    [Function(CallingConventions.Microsoft)]
    private delegate int SelectSwordSkillCardDelegate(int rank, int vanillaItemId);


    private delegate ushort GetNumPersonasDelegate();
    private delegate Persona* GetProtagPersonaDelegate(short slot);
    private delegate byte GetPersonaArcanaDelegate(ushort personaId);
    private delegate int GetPersonaRequiredExpDelegate(Persona* persona, ushort level);

    [StructLayout(LayoutKind.Explicit, Size = 0x38)]
    private struct ItemTblEntry
    {
        [FieldOffset(0x00)]
        public int DescriptionTable;

        [FieldOffset(0x24)]
        public ushort Skill;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x34)]
    private struct Persona
    {
        [FieldOffset(0x00)]
        public byte IsRegistered;

        [FieldOffset(0x02)]
        public short Id;

        [FieldOffset(0x04)]
        public byte Level;

        [FieldOffset(0x08)]
        public int Exp;

        [FieldOffset(0x0C)]
        public fixed short Skills[8];

        [FieldOffset(0x1C)]
        public PersonaStats Stats;

        [FieldOffset(0x21)]
        public PersonaStats BonusStats;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PersonaStats
    {
        public byte Strength;
        public byte Magic;
        public byte Endurance;
        public byte Agility;
        public byte Luck;
    }

}
