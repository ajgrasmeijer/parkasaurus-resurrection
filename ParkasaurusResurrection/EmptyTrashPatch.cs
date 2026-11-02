using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ParkasaurusResurrection
{
    // Shift + left-click on the trash slot of the inventory window empties the whole window after an
    // "are you sure" prompt: the inventory (ParkFile.m_inventory) or, in Storage (crate mode), all
    // crated dinos (m_dinoCrated). Uses the game's own delete prompt and clean-up (InvMainUI.TryDelete
    // and the trash slot's InvSlotUI.OnDrop), including dropping a crated dino's SavedDinosaur.
    public static class EmptyTrashPatch
    {
        private static bool s_asking;

        [HarmonyPatch(typeof(InvSlotUI), nameof(InvSlotUI.OnPointerClick))]
        public static class OnClick
        {
            public static void Postfix(InvSlotUI __instance, PointerEventData eventData)
            {
                if (!ModConfig.ShiftEmptiesTrash.Value || !__instance.m_isTrash || s_asking
                    || eventData.button != PointerEventData.InputButton.Left || InvItemUI.m_dragged != null)
                    return;
                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                if (keyboard == null || !keyboard.shiftKey.isPressed)
                    return;
                InvMainUI window = UICore.Get.m_inventory;
                if (window != null && window.gameObject.activeInHierarchy)
                    window.StartCoroutine(AskAndEmpty(window));
            }
        }

        private static IEnumerator AskAndEmpty(InvMainUI window)
        {
            bool storage = window.m_crateMode;
            List<ZooItem> items = storage ? GameCore.Get.m_park.m_dinoCrated : GameCore.Get.m_park.m_inventory;
            int count = 0;
            foreach (ZooItem item in items)
                count += Mathf.Max(1, item.m_amount);
            if (count == 0)
                yield break;

            s_asking = true;
            try
            {
                string what = storage ? "your storage" : "your inventory";
                yield return window.StartCoroutine(UICore.Get.m_messagebox.ShowMessage(GameCore.Translate("Delete"),
                    $"Delete all {count} item{(count == 1 ? "" : "s")} in {what}? This can't be undone."));
                if (UICore.Get.m_messageBoxAnswer == 2)
                {
                    foreach (ZooItem item in items)
                        if (item.m_zooitem != null && item.m_zooitem.m_dinoBoxed is SavedDinosaur dino)
                            GameCore.Get.m_park.m_crateDinosaur.Remove(dino);
                    items.Clear();
                    UICore.Get.HoldThis(null, null, saveHold: false);
                    UICore.Get.HideItemHover();
                    window.MakeDirty();
                    Plugin.Log.LogInfo($"Trash: deleted {count} items from {(storage ? "storage" : "the inventory")}");
                }
                UICore.Get.m_messagebox.gameObject.SetActive(false);
            }
            finally
            {
                s_asking = false;
            }
        }
    }
}
