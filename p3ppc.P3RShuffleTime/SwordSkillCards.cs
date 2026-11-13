using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Hooks.Definitions.X64;

namespace p3ppc.P3RShuffleTime;
public unsafe partial class Mod
{
    private const int SkillCardItemBase = 0x700, SkillCardItemLast = 0x7FE, PcSkillCardTableBias = 0x70;

    private SkillCardRewardTable _skillCardRewards = new();
    private short _swordRewardItem = -1;

    private IHook<GetNumItemDelegate>? _getNumItemHook;
    private IHook<SetItemNumDelegate>? _setItemNumHook;
    private IHook<GetItemNameDelegate>? _getItemNameHook;
    private IAsmHook? _swordItemHook;
    private IReverseWrapper<SelectSwordItemDelegate>? _selectSwordWrapper;

    private static bool IsSkillCard(int itemId) => itemId >= SkillCardItemBase && itemId <= SkillCardItemLast;

    private static int ToPcItemIndex(int itemId) => itemId - PcSkillCardTableBias;

    private void LoadSkillCardRewards()
    {
        string path = Path.Combine(Path.GetDirectoryName(typeof(Mod).Assembly.Location) ?? AppContext.BaseDirectory, "skill_cards.json");
        try
        {
            _skillCardRewards = SkillCardRewardTable.Load(path);
        }
        catch (Exception e)
        {
            _skillCardRewards = new SkillCardRewardTable();
            Utils.LogError("Couldn't load skill_cards.json, so Sword cards give their normal rewards", e);
        }
    }

    private void SetupSwordSkillCards()
    {
        Utils.SigScan("40 53 48 83 EC 20 48 0F BF D9 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 4C 8B C3 48 6B D3 38 83 3C 02 05", "GetNumItem", address =>
        {
            _getNumItemHook = _hooks.CreateHook<GetNumItemDelegate>(GetNumItem, address).Activate();
        });

        Utils.SigScan("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 0F B7 F9 0F B6 DA 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? BA 63 00 00 00", "SetItemNum", address =>
        {
            _setItemNumHook = _hooks.CreateHook<SetItemNumDelegate>(SetItemNum, address).Activate();
        });

        Utils.SigScan("E8 ?? ?? ?? ?? 48 8D 15 ?? ?? ?? ?? 48 8B C8 0F B6 84 ?? ?? ?? ?? ??", "Sword Reward Item Name", address =>
        {
            _getItemNameHook = _hooks.CreateHook<GetItemNameDelegate>(GetItemName, (nint)Utils.GetGlobalAddress(address + 1)).Activate();
        });

        // Swaps the Sword reward's item ID for the Skill Card and keeps everything else the game is holding
        Utils.SigScan("8B 75 67 45 8B FC 49 63 C0 48 0F BE 4C 05 AF 45 0F B7 2C CE 44 8B 75 7F 8B 7D 77 4C 8D 05 ?? ?? ?? ??", "Sword Reward Item ID", address =>
        {
            string[] asm =
            {
                "use64",
                "push rax", "push rcx", "push rdx", "push r8", "push r9", "push r10", "push r11",
                "sub rsp, 0x68",
                "movdqu [rsp], xmm0", "movdqu [rsp + 0x10], xmm1", "movdqu [rsp + 0x20], xmm2",
                "movdqu [rsp + 0x30], xmm3", "movdqu [rsp + 0x40], xmm4", "movdqu [rsp + 0x50], xmm5",
                "mov rax, [rbp + 0x67]",
                "test rax, rax",
                "jz keepSwordReward",
                "cmp word [rax + 0x18], 0",
                "jne keepSwordReward",
                "movzx ecx, byte [rax + 0x20]",
                "movzx edx, r13w",
                "sub rsp, 0x20",
                _hooks.Utilities.GetAbsoluteCallMnemonics(SelectSwordItem, out _selectSwordWrapper),
                "add rsp, 0x20",
                $"cmp eax, 0x{SkillCardItemBase:X}",
                "jb keepSwordReward",
                $"cmp eax, 0x{SkillCardItemLast:X}",
                "ja keepSwordReward",
                "movzx r13d, ax",
                "label keepSwordReward",
                "movdqu xmm0, [rsp]", "movdqu xmm1, [rsp + 0x10]", "movdqu xmm2, [rsp + 0x20]",
                "movdqu xmm3, [rsp + 0x30]", "movdqu xmm4, [rsp + 0x40]", "movdqu xmm5, [rsp + 0x50]",
                "add rsp, 0x68",
                "pop r11", "pop r10", "pop r9", "pop r8", "pop rdx", "pop rcx", "pop rax",
            };

            _swordItemHook = _hooks.CreateAsmHook(asm, address + 0x0F, AsmHookBehaviour.ExecuteAfter).Activate();
        });
    }

    // Returns the Skill Card to give, or 0 to keep the vanilla weapon
    private int SelectSwordItem(int rank, int vanillaItem)
    {
        try
        {
            _swordRewardItem = -1;
            if (_getNumItemHook == null || _setItemNumHook == null || _getItemNameHook == null)
            {
                return 0;
            }

            rank = Math.Clamp(rank, 1, 10);
            SkillCardRewardEntry? reward = GetPickedSword(rank);
            if (reward == null)
            {
                Utils.LogError($"Sword rank {rank} has no Skill Cards in skill_cards.json, so it gives its normal reward.");
                return 0;
            }

            if (!reward.TryGetItemId(out int itemId) || !IsSkillCard(itemId))
            {
                Utils.LogError($"\"{reward.Name}\" has ItemId \"{reward.ItemId}\", which isn't a Skill Card (0x{SkillCardItemBase:X}-0x{SkillCardItemLast:X}), so the Sword gives its normal reward.");
                return 0;
            }

            Utils.LogDebug($"Sword rank {rank}: {reward.Name} (0x{itemId:X}).");
            _swordRewardItem = (short)itemId;
            return itemId;
        }
        catch (Exception e)
        {
            Utils.LogError("Sword Skill Card selection failed", e);
            _swordRewardItem = -1;
            return 0;
        }
    }

    // The same card the preview showed for the picked slot
    private SkillCardRewardEntry? GetPickedSword(int rank)
    {
        if (TryGetPickedSlot(0, rank, out int slot) && _slotSword[slot] != null)
        {
            return _slotSword[slot];
        }

        return _skillCardRewards.Pick(rank);
    }

    private bool IsSwordReward(short itemId) => _swordRewardItem >= 0 && itemId == _swordRewardItem;

    private uint GetNumItem(short itemId)
    {
        return _getNumItemHook!.OriginalFunction(IsSwordReward(itemId) ? (short)ToPcItemIndex(itemId) : itemId);
    }

    private void SetItemNum(short itemId, byte num)
    {
        _setItemNumHook!.OriginalFunction(IsSwordReward(itemId) ? (short)ToPcItemIndex(itemId) : itemId, num);
    }

    // The name lookup for the obtained message is the reward's last step, so the redirect ends here and the menus
    // afterwards see the normal item IDs
    private nint GetItemName(short itemId)
    {
        if (!IsSwordReward(itemId))
        {
            return _getItemNameHook!.OriginalFunction(itemId);
        }

        _swordRewardItem = -1;
        return _getItemNameHook!.OriginalFunction((short)ToPcItemIndex(itemId));
    }

    [Function(CallingConventions.Microsoft)]
    private delegate int SelectSwordItemDelegate(int rank, int vanillaItem);

    [Function(CallingConventions.Microsoft)]
    private delegate uint GetNumItemDelegate(short itemId);

    [Function(CallingConventions.Microsoft)]
    private delegate void SetItemNumDelegate(short itemId, byte num);

    [Function(CallingConventions.Microsoft)]
    private delegate nint GetItemNameDelegate(short itemId);
}
