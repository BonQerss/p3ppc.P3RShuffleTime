using p3ppc.P3RShuffleTime.Configuration;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Hooks.Definitions.X64;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace p3ppc.P3RShuffleTime;

/// <summary>
/// Shows what a highlighted Sword or Coin card gives and makes the card the player takes give exactly that
/// Every Sword and Coin card is rolled once when the pick opens, card records are 12 bytes each with
/// type (0 Persona, 1 Arcana, 2 blank), suit or Persona ID and rank, suit 0 is Sword and 1 is Coin
/// </summary>
public unsafe partial class Mod
{
    // Preview position in game UI units (480 by 272), with the description under the message for Both
    private const int PreviewX = 40, PreviewY = 214, DescriptionOffsetY = 34;

    private RenderItemDescriptionDelegate? _renderItemDescription;
    private IHook<ShuffleDrawDelegate>? _shuffleDrawHook;
    private IAsmHook? _coinIndexHook;
    private IReverseWrapper<SelectCoinIndexDelegate>? _selectCoinWrapper;
    private IReverseWrapper<PickFrameDelegate>? _pickFrameWrapper;

    private readonly SkillCardRewardEntry?[] _slotSword = new SkillCardRewardEntry?[8];
    private readonly int[] _slotCoin = new int[8];
    private bool _slotsAssigned;
    private nint _shuffleCtx;
    private nint _coinTable;

    private int _lastHoverSlot = -1;
    private int _hoverSlot = -1;
    private HoverKind _hoverKind;
    private int _hoverValue;

    // Shuffle message text, the merged Shuffle BMD registered as a help message file
    private nint _helpRender;
    private nint _registerMessages;
    private nint _setNumberVar;
    private int _shuffleMessageSlot = -1;
    private bool _shuffleMessagesUnavailable;
    private int _swordMessageIndex = -1;
    private int _coinMessageIndex = -1;
    private int _previewItemId = -1;
    private int _previewCoinAmount = -1;

    private void SetupCardPreview()
    {
        Utils.SigScan("48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 60 0F B7 D9 0F 29 74 24 50 48 8D 0D ?? ?? ?? ?? 0F 28 F3 44 89 C7 89 D6 E8", "RenderItemDescription", address =>
        {
            _renderItemDescription = _hooks.CreateWrapper<RenderItemDescriptionDelegate>(address, out _);

            // It ends in the help text renderer (x, y, z, color, p6, p7, 1, help slot, message index), which
            // draws a message from any registered BMD
            _helpRender = Utils.CallTarget(address + 0xA3);
            if (_helpRender == 0)
            {
                Utils.LogError("Couldn't find the help-text renderer, so the Shuffle message text is off.");
            }
        });

        // The Shuffle task's draw callbacks for the cards and the front card, text only shows up when
        // drawn from a draw callback and not from the sequence update
        Utils.SigScan("48 8D 05 ?? ?? ?? ?? 45 31 FF 48 8D 8D 88 B3 00 00 44 89 7D 10 31 D2 44 89 7D 08 48 89 45 28 48 8D 05 ?? ?? ?? ?? 48 89 85 88 00 00 00", "Shuffle Draw", address =>
        {
            _shuffleDrawHook = _hooks.CreateHook<ShuffleDrawDelegate>(ShuffleDraw, (nint)Utils.GetGlobalAddress(address + 0x22)).Activate();
        });

        // Coin result, each rank has up to 16 entries of 12 bytes where type 0 is yen and the amount follows
        // The hook swaps the game's random pick for the index rolled when the pick opened
        Utils.SigScan("0F B6 47 20 48 8D 3C 40 48 C1 E7 06 48 8D 05 ?? ?? ?? ?? 48 03 F8 0F 1F 40 00 66 66 0F 1F 84 00 00 00 00 00 48 63 C3 48 8D 0C 40 48 8D 0C 8F E8 ?? ?? ?? ?? 83 F8 02 74 07 FF C3 83 FB 10 7C E4 E8 ?? ?? ?? ?? 0F BA F0 1F 66 0F 6E C3 0F 57 C9 0F 5B C0 F3 48 0F 2A C8 F3 0F 59 CE F3 0F 59 C8 F3 0F 2C C1 48 63 C8 48 8D 04 49 48 8D 1C 87", "Coin Reward Index", address =>
        {
            _coinTable = (nint)Utils.GetGlobalAddress(address + 0x0F);
            string[] asm =
            {
                "use64",
                "push rcx", "push rdx", "push r8", "push r9", "push r10", "push r11",
                "sub rsp, 0x60",
                "movdqu [rsp], xmm0", "movdqu [rsp + 0x10], xmm1", "movdqu [rsp + 0x20], xmm2",
                "movdqu [rsp + 0x30], xmm3", "movdqu [rsp + 0x40], xmm4", "movdqu [rsp + 0x50], xmm5",
                "mov rcx, rdi",
                "mov edx, ebx",
                "mov r8d, eax",
                "sub rsp, 0x20",
                _hooks.Utilities.GetAbsoluteCallMnemonics(SelectCoinIndex, out _selectCoinWrapper),
                "add rsp, 0x20",
                "movdqu xmm0, [rsp]", "movdqu xmm1, [rsp + 0x10]", "movdqu xmm2, [rsp + 0x20]",
                "movdqu xmm3, [rsp + 0x30]", "movdqu xmm4, [rsp + 0x40]", "movdqu xmm5, [rsp + 0x50]",
                "add rsp, 0x60",
                "pop r11", "pop r10", "pop r9", "pop r8", "pop rdx", "pop rcx",
            };

            _coinIndexHook = _hooks.CreateAsmHook(asm, address + 0x64, AsmHookBehaviour.ExecuteFirst).Activate();
        });
    }

    // Calls OnPickFrame on every frame of the pick (state 8)
    private string[] BuildPickFrameAsm()
    {
        return new[]
        {
            $"cmp dword [rcx + 0x{SequenceState:X}], 8",
            "jne pickFrameDone",
            "push rcx", "push rdx", "push r8", "push r9",
            "sub rsp, 0x40",
            "movdqu [rsp], xmm0", "movdqu [rsp + 0x10], xmm1", "movdqu [rsp + 0x20], xmm2", "movdqu [rsp + 0x30], xmm3",
            "sub rsp, 0x28",
            _hooks.Utilities.GetAbsoluteCallMnemonics(OnPickFrame, out _pickFrameWrapper),
            "add rsp, 0x28",
            "movdqu xmm0, [rsp]", "movdqu xmm1, [rsp + 0x10]", "movdqu xmm2, [rsp + 0x20]", "movdqu xmm3, [rsp + 0x30]",
            "add rsp, 0x40",
            "pop r9", "pop r8", "pop rdx", "pop rcx",
            "label pickFrameDone",
        };
    }

    private void OnPickFrame(nint ctx)
    {
        try
        {
            int slot = *(int*)(ctx + Cursor);
            if (slot < 0 || slot >= *(int*)(ctx + CardCount) || slot > 7)
            {
                return;
            }

            if (!_slotsAssigned || _shuffleCtx != ctx)
            {
                AssignSlotRewards(ctx);
            }

            TryReadCard(ctx, slot, out int type, out int suit, out int rank);
            SkillCardRewardEntry? sword = type == 1 && suit == 0 ? _slotSword[slot] : null;
            int coin = type == 1 && suit == 1 ? _slotCoin[slot] : -1;

            if (slot != _lastHoverSlot)
            {
                _lastHoverSlot = slot;
                if (sword != null)
                {
                    Utils.LogDebug($"Card {slot}: {sword.Name}.");
                }
                else if (coin >= 0)
                {
                    Utils.LogDebug($"Card {slot}: {CoinYen(rank, coin)} yen.");
                }
            }

            _hoverSlot = slot;
            _hoverKind = HoverKind.None;
            if (sword != null && sword.TryGetItemId(out int itemId) && IsSkillCard(itemId))
            {
                _hoverKind = HoverKind.Sword;
                _hoverValue = itemId;
            }
            else if (coin >= 0)
            {
                _hoverKind = HoverKind.Coin;
                _hoverValue = CoinYen(rank, coin);
            }

            if (_hoverKind != HoverKind.None && _configuration.PreviewText != PreviewTextMode.ItemDescription)
            {
                PrepareShuffleMessage(ctx);
            }
        }
        catch (Exception e)
        {
            Utils.LogError("Card preview failed", e);
        }
    }

    private void ShuffleDraw(nint unused, nint task)
    {
        _shuffleDrawHook!.OriginalFunction(unused, task);
        try
        {
            // Checked against the live cursor every draw, so moving to a card with nothing to show clears the text
            nint ctx = task + 0xE0;
            if (_hoverKind == HoverKind.None || ctx != _shuffleCtx || *(int*)(ctx + SequenceState) != 8 || *(int*)(ctx + Cursor) != _hoverSlot)
            {
                return;
            }

            PreviewTextMode mode = _configuration.PreviewText;
            bool message = mode != PreviewTextMode.ItemDescription && TryDrawShuffleMessage(PreviewX, PreviewY);
            if (_hoverKind != HoverKind.Sword || (message && mode == PreviewTextMode.ShuffleMessage) || _renderItemDescription == null)
            {
                return;
            }

            // The game's own description box takes the PC item index
            _renderItemDescription((ushort)ToPcItemIndex(_hoverValue), PreviewX, message ? PreviewY + DescriptionOffsetY : PreviewY, 0f, unchecked((int)0xFFFFFFFF), 1, 0);
        }
        catch (Exception e)
        {
            Utils.LogError("Card preview drawing failed", e);
        }
    }

    // The card the player took, the cursor still points at it while the result plays
    private bool TryGetPickedSlot(int suit, int rank, out int slot)
    {
        slot = -1;
        if (!_slotsAssigned || _shuffleCtx == 0)
        {
            return false;
        }

        int cursor = *(int*)(_shuffleCtx + Cursor);
        if (cursor < 0 || cursor > 7 || !TryReadCard(_shuffleCtx, cursor, out int type, out int cardSuit, out int cardRank) || type != 1 || cardSuit != suit || cardRank != rank)
        {
            return false;
        }

        slot = cursor;
        return true;
    }

    private static bool TryReadCard(nint ctx, int slot, out int type, out int suit, out int rank)
    {
        nint card = ctx + Cards + slot * 12;
        type = *(int*)card;
        suit = *(short*)(card + 4);
        rank = *(short*)(card + 6);
        return slot < *(int*)(ctx + CardCount);
    }

    // Cards of the same rank get different Skill Cards or yen amounts while the pool allows it
    private void AssignSlotRewards(nint ctx)
    {
        _slotsAssigned = true;
        _shuffleCtx = ctx;
        int count = Math.Min(*(int*)(ctx + CardCount), 8);
        HashSet<string> usedCards = new();
        HashSet<(int, int)> usedYen = new();
        for (int slot = 0; slot < count; slot++)
        {
            if (!TryReadCard(ctx, slot, out int type, out int suit, out int rank) || type != 1 || rank < 1 || rank > 10)
            {
                continue;
            }

            if (suit == 0 && _configuration.EnableSwordSkillCards)
            {
                SkillCardRewardEntry? pick = _skillCardRewards.Pick(rank, usedCards);
                if (pick != null)
                {
                    usedCards.Add(pick.ItemId);
                    _slotSword[slot] = pick;
                }
            }
            else if (suit == 1 && _coinTable != 0)
            {
                int index = RollCoin(rank, usedYen);
                if (index >= 0)
                {
                    usedYen.Add((rank, CoinYen(rank, index)));
                    _slotCoin[slot] = index;
                }
            }
        }
    }

    // Same rules as the game's roll, every entry before the end marker (max 16) is equally likely
    private int RollCoin(int rank, HashSet<(int, int)> usedYen)
    {
        int count = CoinCount(rank);
        if (count <= 0)
        {
            return -1;
        }

        List<int> fresh = new();
        for (int i = 0; i < count; i++)
        {
            if (!usedYen.Contains((rank, CoinYen(rank, i))))
            {
                fresh.Add(i);
            }
        }

        return fresh.Count > 0 ? fresh[Random.Shared.Next(fresh.Count)] : Random.Shared.Next(count);
    }

    private int CoinCount(int rank)
    {
        nint row = _coinTable + rank * 0xC0;
        int count = 0;
        while (count < 16 && *(short*)(row + count * 12) != -1)
        {
            // Only yen entries are understood, anything else is left to vanilla
            if (*(short*)(row + count * 12) != 0)
            {
                return 0;
            }

            count++;
        }

        return count;
    }

    private int CoinYen(int rank, int index) => *(int*)(_coinTable + rank * 0xC0 + index * 12 + 4);

    private int SelectCoinIndex(nint row, int count, int vanillaIndex)
    {
        try
        {
            long offset = row - _coinTable;
            if (_coinTable == 0 || offset <= 0 || offset % 0xC0 != 0)
            {
                return vanillaIndex;
            }

            int rank = (int)(offset / 0xC0);
            if (!TryGetPickedSlot(1, rank, out int slot) || _slotCoin[slot] < 0 || _slotCoin[slot] >= count)
            {
                return vanillaIndex;
            }

            return _slotCoin[slot];
        }
        catch (Exception e)
        {
            Utils.LogError("Coin reward selection failed", e);
            return vanillaIndex;
        }
    }

    // The message register every help BMD and the Shuffle window go through, reached through the game's own
    // chain where the look away prompt opens the window and the window's second call registers
    private void ResolveMessageRegister(nint sequence)
    {
        ReadOnlySpan<byte> loadsShuffleBmd = new byte[] { 0x48, 0x8B, 0x8B, 0x48, 0xB2, 0x00, 0x00 };
        nint prompt = Utils.CallTarget(sequence + 0x1CB);
        if (prompt == 0 || !Utils.IsReadable(prompt, 0x30) || !new ReadOnlySpan<byte>((void*)(prompt + 0x1E), 7).SequenceEqual(loadsShuffleBmd))
        {
            Utils.LogError("Couldn't find the look-away prompt, so the Shuffle message text is off.");
            return;
        }

        nint open = Utils.FollowThunks(Utils.CallTarget(prompt + 0x25));
        _registerMessages = open != 0 ? Utils.FollowThunks(Utils.CallTarget(open + 0x3E)) : 0;
        if (_registerMessages == 0)
        {
            Utils.LogError("Couldn't find the message register, so the Shuffle message text is off.");
        }
    }

    // The numeric message var setter (slot, var, value), found through the one script native that fills vars
    // 0x0E to 0x11 with four calls to it, then checked against what the setter does
    private void ResolveNumberSetter(nint sequence)
    {
        if (!TryGetSection(sequence, out nint start, out int length))
        {
            Utils.LogError("Couldn't read the code section, so the Skill Card message text is off.");
            return;
        }

        ReadOnlySpan<byte> code = new((void*)start, length);
        ReadOnlySpan<byte> firstVar = new byte[] { 0xBA, 0x0E, 0x00, 0x00, 0x00 };
        nint setter = 0;
        int matches = 0;
        for (int pos = 0; ;)
        {
            int hit = code[pos..].IndexOf(firstVar);
            if (hit < 0)
            {
                break;
            }

            hit += pos;
            pos = hit + 1;
            nint target = MatchVarCalls(code, start, hit);
            if (target != 0 && target != setter)
            {
                setter = target;
                matches++;
            }
        }

        if (matches != 1 || !LooksLikeNumberSetter(setter))
        {
            Utils.LogError("Couldn't find the message number setter, so the Skill Card message text is off.");
            return;
        }

        _setNumberVar = setter;
    }

    // Each of vars 0x0E to 0x11 is followed by a call to the same function
    private static nint MatchVarCalls(ReadOnlySpan<byte> code, nint start, int first)
    {
        const int Window = 24;
        nint target = 0;
        int from = first;
        for (int var = 0x0E; var <= 0x11; var++)
        {
            int mov = -1;
            for (int i = from; i < from + Window && i + 5 <= code.Length; i++)
            {
                if (code[i] == 0xBA && code[i + 1] == var && code[i + 2] == 0 && code[i + 3] == 0 && code[i + 4] == 0)
                {
                    mov = i;
                    break;
                }
            }

            if (mov < 0)
            {
                return 0;
            }

            int call = -1;
            for (int i = mov + 5; i < mov + 5 + Window && i + 5 <= code.Length; i++)
            {
                if (code[i] != 0xE8)
                {
                    continue;
                }

                nint t = Utils.FollowThunks(start + i + 5 + BitConverter.ToInt32(code.Slice(i + 1, 4)));
                if (t != 0 && (target == 0 || t == target))
                {
                    target = t;
                    call = i;
                    break;
                }
            }

            if (call < 0)
            {
                return 0;
            }

            from = call + 5;
        }

        return target;
    }

    // The setter bounds slot to 0x7F and var to 0x1F, then stores a 4 byte buffer and its size for the var
    private static bool LooksLikeNumberSetter(nint function)
    {
        const int Length = 0x100;
        if (function == 0 || !Utils.IsReadable(function, Length + 11))
        {
            return false;
        }

        byte* p = (byte*)function;
        bool slotCheck = false, varCheck = false, buffer = false, size = false;
        for (int i = 0; i < Length; i++)
        {
            // Slot and var bounds checks
            if (p[i] == 0x83 && p[i + 1] >= 0xF8)
            {
                slotCheck |= p[i + 2] == 0x7F;
                varCheck |= p[i + 2] == 0x1F;
            }

            // Buffer store
            if ((p[i] & 0xF0) == 0x40 && p[i + 1] == 0x89 && (p[i + 2] & 0xC7) == 0x84 && p[i + 4] == 0xB8 && p[i + 5] == 0 && p[i + 6] == 0 && p[i + 7] == 0)
            {
                buffer = true;
            }

            // Size store
            if (p[i] == 0xC7 && p[i + 1] == 0x84 && p[i + 3] == 0xB8 && p[i + 4] == 0x01 && p[i + 5] == 0 && p[i + 6] == 0 && p[i + 7] == 4 && p[i + 8] == 0 && p[i + 9] == 0 && p[i + 10] == 0)
            {
                size = true;
            }
        }

        return slotCheck && varCheck && buffer && size;
    }

    private static bool TryGetSection(nint address, out nint start, out int length)
    {
        start = 0;
        length = 0;
        nint image = Utils.BaseAddress;
        if (!Utils.IsReadable(image, 0x400))
        {
            return false;
        }

        nint nt = image + *(int*)(image + 0x3C);
        int sections = *(ushort*)(nt + 6);
        nint table = nt + 0x18 + *(ushort*)(nt + 0x14);
        for (int i = 0; i < sections; i++)
        {
            nint section = table + i * 0x28;
            nint begin = image + *(int*)(section + 0x0C);
            int size = *(int*)(section + 0x08);
            if (address >= begin && address < begin + size)
            {
                start = begin;
                length = size;
                return Utils.IsReadable(begin, size);
            }
        }

        return false;
    }

    // Registers the message file once and sets the message vars when the highlighted value changes
    private void PrepareShuffleMessage(nint ctx)
    {
        if (_helpRender == 0 || _registerMessages == 0 || _setNumberVar == 0 || _shuffleMessagesUnavailable)
        {
            return;
        }

        if (_shuffleMessageSlot < 0 && !RegisterShuffleMessages(ctx))
        {
            _shuffleMessagesUnavailable = true;
            return;
        }

        var setVar = (delegate* unmanaged<uint, uint, uint, void>)_setNumberVar;
        uint slot = (uint)_shuffleMessageSlot;
        if (_hoverKind == HoverKind.Sword)
        {
            // [itemVar 3 65535 0] reads var 0 as a native item index, the one GetItemName takes
            int itemId = ToPcItemIndex(_hoverValue);
            if (itemId != _previewItemId)
            {
                setVar(slot, 0, (uint)itemId);
                _previewItemId = itemId;
            }
        }
        else if (_hoverKind == HoverKind.Coin && _coinMessageIndex >= 0 && _hoverValue != _previewCoinAmount)
        {
            // [var 1][var 2] print each var as a string, a numeric var is a 4 byte buffer so each one carries up
            // to 3 digits plus the terminator, so 92500 is " 92" and "500"
            string digits = _hoverValue.ToString(CultureInfo.InvariantCulture);
            string head = " " + digits[..Math.Max(0, digits.Length - 3)];
            string tail = digits[Math.Max(0, digits.Length - 3)..];
            if (head.Length > 3)
            {
                return;
            }

            setVar(slot, 1, PackDigits(head));
            setVar(slot, 2, PackDigits(tail));
            _previewCoinAmount = _hoverValue;
        }
    }

    private static uint PackDigits(string text)
    {
        uint value = 0;
        for (int i = 0; i < text.Length; i++)
        {
            value |= (uint)text[i] << (8 * i);
        }

        return value;
    }

    private bool TryDrawShuffleMessage(int x, int y)
    {
        int index;
        if (_hoverKind == HoverKind.Sword)
        {
            if (_shuffleMessageSlot < 0 || _previewItemId != ToPcItemIndex(_hoverValue))
            {
                return false;
            }

            index = _swordMessageIndex;
        }
        else
        {
            if (_shuffleMessageSlot < 0 || _coinMessageIndex < 0 || _previewCoinAmount != _hoverValue)
            {
                return false;
            }

            index = _coinMessageIndex;
        }

        var render = (delegate* unmanaged<float, float, float, int, int, int, int, int, int, long>)_helpRender;
        render(x, y, 0f, unchecked((int)0xFFFFFFFF), 1, 0, 1, _shuffleMessageSlot, index);
        return true;
    }

    // Registers a copy of the merged Shuffle BMD since the game frees its own after every Shuffle
    private bool RegisterShuffleMessages(nint ctx)
    {
        nint bmd = *(nint*)(ctx + ShuffleBmd);
        if (bmd == 0 || !Utils.IsReadable(bmd, 0x20) || *(uint*)(bmd + 8) != 0x3147534D)
        {
            Utils.LogError("The Shuffle message file isn't loaded, so the item description is used instead.");
            return false;
        }

        int size = *(int*)(bmd + 4);
        if (size < 0x20 || size > 0x400000 || !Utils.IsReadable(bmd, size))
        {
            Utils.LogError("The Shuffle message file looks wrong, so the item description is used instead.");
            return false;
        }

        int index = FindMessage(bmd, size, "P3RST_SWORD");
        if (index < 0)
        {
            Utils.LogError("The Shuffle message file has no P3RST_SWORD message (is Persona Essentials merging it?), so the item description is used instead.");
            return false;
        }

        nint copy = Marshal.AllocHGlobal(size);
        Buffer.MemoryCopy((void*)bmd, (void*)copy, size, size);

        int slot = ((delegate* unmanaged<nint, int>)_registerMessages)(copy);
        if (slot < 0 || slot > 0x7F)
        {
            Utils.LogError("Couldn't register the Shuffle message file, so the item description is used instead.");
            return false;
        }

        _shuffleMessageSlot = slot;
        _swordMessageIndex = index;
        _coinMessageIndex = FindMessage(bmd, size, "P3RST_COIN");
        _previewItemId = -1;
        _previewCoinAmount = -1;
        if (_coinMessageIndex < 0)
        {
            Utils.LogError("The Shuffle message file has no P3RST_COIN message, so Coin cards show no text.");
        }

        return true;
    }

    // MSG1 has the dialog count at +0x18, then kind and offset pairs at +0x20, offsets are relative to +0x20 and a
    // message dialog starts with its 24 byte name
    private static int FindMessage(nint bmd, int size, string wanted)
    {
        int count = *(int*)(bmd + 0x18);
        if (count <= 0 || 0x20 + (long)count * 8 > size)
        {
            return -1;
        }

        byte[] key = Encoding.ASCII.GetBytes(wanted + "\0");
        for (int i = 0; i < count; i++)
        {
            int kind = *(int*)(bmd + 0x20 + i * 8);
            uint offset = *(uint*)(bmd + 0x24 + i * 8);
            if (kind == 0 && 0x20 + (long)offset + 24 <= size && new ReadOnlySpan<byte>((void*)(bmd + 0x20 + (nint)offset), key.Length).SequenceEqual(key))
            {
                return i;
            }
        }

        return -1;
    }

    private enum HoverKind
    {
        None,
        Sword,
        Coin,
    }

    [Function(CallingConventions.Microsoft)]
    private delegate void RenderItemDescriptionDelegate(ushort item, int x, int y, float unused, int color, int p6, int p7);

    [Function(CallingConventions.Microsoft)]
    private delegate void PickFrameDelegate(nint ctx);

    [Function(CallingConventions.Microsoft)]
    private delegate void ShuffleDrawDelegate(nint unused, nint task);

    [Function(CallingConventions.Microsoft)]
    private delegate int SelectCoinIndexDelegate(nint row, int count, int vanillaIndex);
}
