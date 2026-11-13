using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using System.Runtime.InteropServices;

namespace p3ppc.P3RShuffleTime;

public unsafe partial class Mod
{
    private IHook<UpdateShufflePersonaResultDelegate>? _updateShufflePersonaResultHook;
    private GetNumPersonasDelegate? _getNumPersonas;
    private GetProtagPersonaDelegate? _getProtagPersona;
    private GetPersonaArcanaDelegate? _getPersonaArcana;
    private GetPersonaRequiredExpDelegate? _getPersonaRequiredExp;
    private bool _rewardProcessedThisShuffle;

    private void SetupDuplicatePersona()
    {
        Utils.SigScan("40 53 56 57 48 83 EC 40 48 89 CB", "UpdateShufflePersonaResult", address =>
        {
            _updateShufflePersonaResultHook = _hooks.CreateHook<UpdateShufflePersonaResultDelegate>(UpdateShufflePersonaResult, address).Activate();
        });

        Utils.SigScan("40 53 48 83 EC 20 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 31 DB E8 ?? ?? ?? ??", "GetNumPersonas", address =>
        {
            _getNumPersonas = _hooks.CreateWrapper<GetNumPersonasDelegate>(address, out _);
        });

        Utils.SigScan("E8 ?? ?? ?? ?? 0F B6 C8 E8 ?? ?? ?? ?? 48 63 C8", "GetPersonaArcana", address =>
        {
            _getPersonaArcana = _hooks.CreateWrapper<GetPersonaArcanaDelegate>((nint)Utils.GetGlobalAddress(address + 1), out _);
        });

        Utils.SigScan("40 53 48 83 EC 20 0F B7 D9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 66 85 DB 78 ?? E8 ?? ?? ?? ?? 0F B7 D0 0F BF C3 39 D0 7C ?? 8B 15 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? FF C2 E8 ?? ?? ?? ?? 48 0F BF C3 48 6B C8 34 48 8D 05 ?? ?? ?? ?? 48 01 C8", "GetProtagPersona", address =>
        {
            _getProtagPersona = _hooks.CreateWrapper<GetProtagPersonaDelegate>(address, out _);
        });

        Utils.SigScan("E8 ?? ?? ?? ?? BB C0 00 00 00 41 3B C7", "GetPersonaRequiredExp", address =>
        {
            _getPersonaRequiredExp = _hooks.CreateWrapper<GetPersonaRequiredExpDelegate>((nint)Utils.GetGlobalAddress(address + 1), out _);
        });
    }

    // The result revisits state 0 many times, so the reward is given once per Shuffle Time
    private uint UpdateShufflePersonaResult(nint result)
    {
        try
        {
            if (*(int*)(result + 0x10) == 0 && !_rewardProcessedThisShuffle)
            {
                _rewardProcessedThisShuffle = true;
                short personaId = *(short*)(result + 0x18);
                if (_configuration.EnableDuplicatePersonaArcanaExp && _getNumPersonas != null && _getProtagPersona != null && _getPersonaArcana != null && _getPersonaRequiredExp != null && FindOwnedPersona(personaId) != null)
                {
                    ApplyDuplicateArcanaReward(personaId, _getPersonaArcana((ushort)personaId));
                }
            }
        }
        catch (Exception e)
        {
            Utils.LogError("Duplicate Persona reward failed", e);
        }

        return _updateShufflePersonaResultHook!.OriginalFunction(result);
    }

    private Persona* FindOwnedPersona(short personaId)
    {
        ushort count = _getNumPersonas!();
        for (short slot = 0; slot < count; slot++)
        {
            Persona* persona = _getProtagPersona!(slot);
            if (persona != null && persona->Id == personaId)
            {
                return persona;
            }
        }

        return null;
    }

    // EXP is added directly, level ups happen on the next normal EXP award
    private void ApplyDuplicateArcanaReward(short selectedPersonaId, byte arcana)
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

            if ((!_configuration.IncludeSelectedPersona && persona->Id == selectedPersonaId) || _getPersonaArcana!((ushort)persona->Id) != arcana)
            {
                continue;
            }

            int exp = CalculateRewardExp(persona);
            if (exp > 0)
            {
                persona->Exp += exp;
                affected++;
            }
        }

        Utils.LogDebug($"Duplicate Persona {selectedPersonaId}: gave Arcana {arcana} EXP to {affected} Persona(s).");
    }

    // 15 to 25 percent of the EXP left to the next level, I do not have a P3R Ghidra project to confirm the actual number so this is a guess
    private int CalculateRewardExp(Persona* persona)
    {
        int remaining = _getPersonaRequiredExp!(persona, (ushort)(persona->Level + 1)) - persona->Exp;
        if (remaining <= 1)
        {
            return 0;
        }

        double percent = 0.15 + Random.Shared.NextDouble() * 0.10;
        return Math.Clamp((int)Math.Floor(remaining * percent), 0, remaining - 1);
    }

    [Function(CallingConventions.Microsoft)]
    private delegate uint UpdateShufflePersonaResultDelegate(nint result);

    [Function(CallingConventions.Microsoft)]
    private delegate ushort GetNumPersonasDelegate();

    [Function(CallingConventions.Microsoft)]
    private delegate Persona* GetProtagPersonaDelegate(short slot);

    [Function(CallingConventions.Microsoft)]
    private delegate byte GetPersonaArcanaDelegate(ushort personaId);

    [Function(CallingConventions.Microsoft)]
    private delegate int GetPersonaRequiredExpDelegate(Persona* persona, ushort level);

    [StructLayout(LayoutKind.Explicit, Size = 0x34)]
    private struct Persona
    {
        [FieldOffset(0x02)]
        public short Id;

        [FieldOffset(0x04)]
        public byte Level;

        [FieldOffset(0x08)]
        public int Exp;
    }
}
