using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ParkasaurusResurrection
{
    // Right-clicking an item in an equipment slot puts it back in the inventory: the hat slot of a
    // dino or staff member, and the item slots of an egg. Does the same as the game's own way of
    // dragging the item out onto an inventory slot (InvSlotUI.OnDrop): the item goes back into the
    // park inventory and the panel gets an InvChange, so DinoPaneUI / EmployeePaneUI call
    // EquipHead(null) and EggUI clears m_eggItem0-2.
    // A right-click over the UI doesn't close the panel (UICore only backs out when !m_isOverUI).
    public static class RightClickUnequipPatch
    {
        [HarmonyPatch(typeof(InvItemUI), nameof(InvItemUI.OnPointerClick))]
        public static class OnClick
        {
            public static void Postfix(InvItemUI __instance, PointerEventData eventData)
            {
                if (!ModConfig.RightClickUnequip.Value || eventData.button != PointerEventData.InputButton.Right
                    || __instance.m_disabled || InvItemUI.m_dragged != null)
                    return;
                InvSlotUI slot = __instance.transform.parent != null ? __instance.transform.parent.GetComponent<InvSlotUI>() : null;
                ZooItemObject item = __instance.m_item?.m_zooitem;
                if (slot == null || !slot.m_receiveDrop || item == null)
                    return;

                GameCore.Get.m_park.AddItem(item, Vector3.zero, world: false, quiet: true);
                // Out of the slot right away (the slot reads its first child), destroyed at the end of the frame
                GameObject go = __instance.gameObject;
                go.SetActive(false);
                go.transform.SetParent(null, false);
                Object.Destroy(go);

                UICore.Get.HideItemHover();
                UICore.Get.m_inventory.MakeDirty();
                slot.UnHighlight();
                ExecuteEvents.ExecuteHierarchy<IInvChange>(slot.gameObject, null, (x, y) => x.InvChange());
                GameCore.Get.PlaySound(Global2.Get.m_soundUIPickUp);
            }
        }
    }
}
