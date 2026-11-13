using p3ppc.P3RShuffleTime.Configuration;
using p3ppc.P3RShuffleTime.Template;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Mod.Interfaces;
using System.Runtime.InteropServices;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p3ppc.P3RShuffleTime;

public unsafe partial class Mod : ModBase
{
    // Shuffle sequence context 
    private const int SequenceState = 0xB280, Cursor = 0xAF6C, CardCount = 0xB290, Cards = 0xB180, ShuffleBmd = 0xB248;
    private const int TutorialSeenFlag = 0x1428;

    private readonly IModLoader _modLoader;
    private readonly IReloadedHooks _hooks;
    private Config _configuration;

    private IAsmHook? _shuffleStartHook;
    private IAsmHook? _sequenceHook;
    private IAsmHook? _hideCountdownHook;
    private IAsmHook? _keepTimerWaitingHook;
    private IReverseWrapper<ResetShuffleDelegate>? _resetShuffleWrapper;

    private BitCheckDelegate? _bitCheck;
    private nint _tutorialFlag;
    private nint _lookAwayFlag;

    public Mod(ModContext context)
    {
        _modLoader = context.ModLoader;
        _configuration = context.Configuration;

        if (!Utils.Initialise(context.Logger, _configuration, _modLoader))
        {
            throw new InvalidOperationException("P3R Shuffle Time couldn't start.");
        }

        var hooksController = _modLoader.GetController<IReloadedHooks>();
        if (hooksController == null || !hooksController.TryGetTarget(out _hooks!))
        {
            throw new InvalidOperationException("Unable to get the Reloaded Hooks controller.");
        }

        if (_configuration.EnableSwordSkillCards)
        {
            LoadSkillCardRewards();
        }

        SetupCardGeneration();
        SetupShuffleStart();
        if (_configuration.EnableSelectableCards)
        {
            SetupSelectableCards();
            if (_configuration.PreviewText != PreviewTextMode.Off)
            {
                SetupCardPreview();
            }
        }

        if (_configuration.EnableSwordSkillCards)
        {
            SetupSwordSkillCards();
        }

        SetupDuplicatePersona();
    }

    private void SetupCardGeneration()
    {
        if (_configuration.DisableBlankCardGeneration)
        {
            // Card count table, the last six (normal, blank) pairs are zeroed so no blanks are rolled
            Utils.SigScan("01 04 01 04 00 00 00 01 01 02 01 03 01 03 02 03 02 04 00 00 B3 00 00 00 F4 01 00 00", "Blank Card Count Table", address =>
            {
                if (!Utils.SafeWrite(address + 6, new byte[12]))
                {
                    Utils.LogError("Couldn't patch the blank card table.");
                }
            });
        }
        else if (_configuration.RegenerateBlankCards)
        {
            // A blank (type 2) slot carries on through the normal card generation instead of staying empty
            Utils.SigScan("4C 3B C7 7D 0B C7 01 02 00 00 00 E9 ?? ?? ?? ?? 4C 3B CB", "Blank Card Branch", address =>
            {
                if (*(byte*)(address + 3) != 0x7D || !Utils.SafeWrite(address + 3, new byte[] { 0xEB }))
                {
                    Utils.LogError("Couldn't patch the blank card branch.");
                }
            });
        }
    }

    private void SetupShuffleStart()
    {
        // Runs once per Shuffle Time, so it's where per Shuffle state is reset and the free pick mode is set
        Utils.SigScan("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 48 8B F9 41 8B D9 48 8D 0D ?? ?? ?? ?? 41 8B F0 48 8B EA", "Shuffle Start", address =>
        {
            List<string> asm = new()
            {
                "use64",
                "push rax", "push rcx", "push rdx", "push r8",
                "sub rsp, 0x28",
                _hooks.Utilities.GetAbsoluteCallMnemonics(ResetShuffle, out _resetShuffleWrapper),
                "add rsp, 0x28",
                "pop r8", "pop rdx", "pop rcx", "pop rax",
            };

            if (_configuration.EnableSelectableCards)
            {
                asm.Add("mov r9d, 3");
            }

            _shuffleStartHook = _hooks.CreateAsmHook(asm.ToArray(), address, AsmHookBehaviour.ExecuteFirst).Activate();
        });
    }

    private void SetupSelectableCards()
    {
        
        Utils.SigScan("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 81 EC 90 00 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F9", "Shuffle Sequence", address =>
        {
            List<string> asm = new() { "use64" };

            ResolveBitCheck(address);
            if (_bitCheck != null)
            {
                _tutorialFlag = AllocateFlag();
            }

            if (_configuration.PreviewText != PreviewTextMode.Off)
            {
                asm.AddRange(BuildPickFrameAsm());
                if (_configuration.PreviewText != PreviewTextMode.ItemDescription)
                {
                    ResolveMessageRegister(address);
                    ResolveNumberSetter(address);
                }
            }

            nint keyPressed = _configuration.EnableLookAway ? ResolveKeyPressed(address, 0x8E0) : 0;
            if (keyPressed != 0)
            {
                // Cancel while picking hands the frame to vanilla state 3, which opens the game's own look away
                // prompt, No returns to state 3 which is skipped back to the pick with the cursor kept
                _lookAwayFlag = AllocateFlag();
                asm.AddRange(new[]
                {
                    $"cmp dword [rcx + 0x{SequenceState:X}], 8",
                    "jne lookAwayDone",
                    $"mov rax, 0x{(long)keyPressed:X}",
                    "test dword [rax], 0x4000",
                    "jz lookAwayDone",
                    "test dword [rax], 0x2000",
                    "jnz lookAwayDone",
                    // hide the countdown like the vanilla confirm does
                    "mov rax, [rcx + 0xB1E8]",
                    "test rax, rax",
                    "jz lookAwayState",
                    "mov rax, [rax + 0x48]",
                    "test rax, rax",
                    "jz lookAwayState",
                    "and word [rax + 0x70], 0xFFEF",
                    "label lookAwayState",
                    $"mov rax, 0x{(long)_lookAwayFlag:X}",
                    "mov byte [rax], 1",
                    $"mov dword [rcx + 0x{SequenceState:X}], 3",
                    "jmp sequenceDone",
                    "label lookAwayDone",
                });
            }
            else if (_configuration.EnableLookAway)
            {
                Utils.LogError("Couldn't find the Shuffle Time input state, so look away is off.");
            }

            asm.AddRange(new[] { $"cmp dword [rcx + 0x{SequenceState:X}], 3", "jne sequenceDone" });
            if (_tutorialFlag != 0)
            {
                asm.AddRange(new[] { $"mov rax, 0x{(long)_tutorialFlag:X}", "cmp byte [rax], 0", "mov byte [rax], 0", "jne sequenceDone" });
            }

            asm.Add($"mov dword [rcx + 0x{SequenceState:X}], 7");
            if (_lookAwayFlag != 0)
            {
                asm.AddRange(new[] { $"mov rax, 0x{(long)_lookAwayFlag:X}", "cmp byte [rax], 0", "mov byte [rax], 0", "jne sequenceDone" });
            }

            asm.AddRange(new[] { $"mov dword [rcx + 0x{Cursor:X}], 0", "label sequenceDone" });
            _sequenceHook = _hooks.CreateAsmHook(asm.ToArray(), address, AsmHookBehaviour.ExecuteFirst).Activate();
        });

        // Skips the countdown start call but keeps the flag it sets
        Utils.SigScan("E8 ?? ?? ?? ?? 66 83 8F ?? ?? ?? ?? 10 48 8B 8F ?? ?? ?? ??", "Shuffle Countdown", address =>
        {
            int nextMov = *(int*)(address + 16);
            string[] asm =
            {
                "use64",
                "or word [rdi + 0xAF52], 0x10",
                nextMov >= 0 ? $"mov rcx, [rdi + 0x{nextMov:X}]" : $"mov rcx, [rdi - 0x{-nextMov:X}]",
                _hooks.Utilities.GetAbsoluteJumpMnemonics(address + 20, true),
            };

            _hideCountdownHook = _hooks.CreateAsmHook(asm, address, AsmHookBehaviour.ExecuteFirst).Activate();
        });

        Utils.SigScan("40 53 48 83 EC 20 48 89 CB 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 43 ?? B9 00 04 00 00", "Shuffle Timer", address =>
        {
            string[] asm =
            {
                "use64",
                "mov rax, [rcx + 0x48]",
                "test rax, rax",
                "jz timerDone",
                "or word [rax + 0x70], 0x400",
                "and byte [rax + 0x08], 0xFD",
                "label timerDone",
            };

            _keepTimerWaitingHook = _hooks.CreateAsmHook(asm, address, AsmHookBehaviour.ExecuteFirst).Activate();
        });
    }

    private static nint AllocateFlag()
    {
        nint flag = Marshal.AllocHGlobal(8);
        Marshal.WriteInt64(flag, 0);
        return flag;
    }

    private void ResetShuffle()
    {
        _swordRewardItem = -1;
        _rewardProcessedThisShuffle = false;
        if (_lookAwayFlag != 0)
        {
            Marshal.WriteByte(_lookAwayFlag, 0);
        }

        Array.Clear(_slotSword);
        Array.Fill(_slotCoin, -1);
        _slotsAssigned = false;
        _lastHoverSlot = -1;
        _hoverSlot = -1;
        _hoverKind = HoverKind.None;
        _shuffleMessagesUnavailable = false;

        if (_tutorialFlag != 0)
        {
            // Until the tutorial has been seen on this save, vanilla state 3 runs once and plays it (it sets the flag)
            bool seen = _bitCheck == null || _bitCheck(TutorialSeenFlag) != 0;
            Marshal.WriteByte(_tutorialFlag, (byte)(seen ? 0 : 1));
        }
    }

    
    private void ResolveBitCheck(nint sequence)
    {
        byte* p = (byte*)(sequence + 0x153);
        if (p[0] != 0xB9 || *(int*)(p + 1) != TutorialSeenFlag || p[5] != 0xE8)
        {
            Utils.LogError("Couldn't find the tutorial flag check, so the Shuffle Time tutorial stays skipped.");
            return;
        }

        _bitCheck = _hooks.CreateWrapper<BitCheckDelegate>(Utils.CallTarget(sequence + 0x158), out _);
    }

    // The sequence tests one input word for both cancel (0x4000) and confirm (0x2000), that's the pressed buttons state
    private static nint ResolveKeyPressed(nint function, int length)
    {
        if (!Utils.IsReadable(function, length + 16))
        {
            return 0;
        }

        HashSet<nint> cancel = new();
        HashSet<nint> confirm = new();
        byte* p = (byte*)function;

        void Add(nint target, uint mask)
        {
            if (mask == 0x4000)
            {
                cancel.Add(target);
            }
            else if (mask == 0x2000)
            {
                confirm.Add(target);
            }
        }

        for (int i = 0; i < length; i++)
        {
            if (p[i] == 0xF7 && p[i + 1] == 0x05)
            {
                Add(function + i + 10 + *(int*)(p + i + 2), *(uint*)(p + i + 6));
            }
            else if (p[i] == 0x66 && p[i + 1] == 0xF7 && p[i + 2] == 0x05)
            {
                Add(function + i + 9 + *(int*)(p + i + 3), *(ushort*)(p + i + 7));
            }
            else if (p[i] == 0xF6 && p[i + 1] == 0x05)
            {
                Add(function + i + 7 + *(int*)(p + i + 2) - 1, (uint)p[i + 6] << 8);
            }
            else
            {
                int start = i;
                if (p[start] == 0x44 || p[start] == 0x41)
                {
                    start++;
                }

                int dispAt;
                if (p[start] == 0x8B && (p[start + 1] & 0xC7) == 0x05)
                {
                    dispAt = start + 2;
                }
                else if (p[start] == 0x0F && p[start + 1] == 0xB7 && (p[start + 2] & 0xC7) == 0x05)
                {
                    dispAt = start + 3;
                }
                else
                {
                    continue;
                }

                nint target = function + dispAt + 4 + *(int*)(p + dispAt);
                byte* t = p + dispAt + 4;
                if (t[0] == 0xA9)
                {
                    Add(target, *(uint*)(t + 1));
                }
                else if (t[0] == 0x66 && t[1] == 0xA9)
                {
                    Add(target, *(ushort*)(t + 2));
                }
                else if (t[0] == 0xF7 && (t[1] & 0xF8) == 0xC0)
                {
                    Add(target, *(uint*)(t + 2));
                }
                else if (t[0] == 0xF6 && (t[1] & 0xFC) == 0xC4)
                {
                    Add(target, (uint)t[2] << 8);
                }
                else if (t[0] == 0x0F && t[1] == 0xBA && (t[2] & 0xF8) == 0xE0)
                {
                    Add(target, 1u << t[3]);
                }
            }
        }

        cancel.IntersectWith(confirm);
        if (cancel.Count != 1)
        {
            return 0;
        }

        nint keyPressed = cancel.First();
        return Utils.IsReadable(keyPressed, 4) ? keyPressed : 0;
    }

    public override void ConfigurationUpdated(Config configuration)
    {
        _configuration = configuration;
        Utils.UpdateConfig(configuration);
    }

    [Function(CallingConventions.Microsoft)]
    private delegate void ResetShuffleDelegate();

    [Function(CallingConventions.Microsoft)]
    private delegate int BitCheckDelegate(int flag);

#pragma warning disable CS8618
    public Mod() { }
#pragma warning restore CS8618
}
