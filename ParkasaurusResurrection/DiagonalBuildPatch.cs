using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ParkasaurusResurrection
{
    // Dragging fences (PlaceType.Connector) or paths diagonally places a staircase of tiles instead
    // of snapping to a straight line. Fences only connect north/east/south/west, so a staircase is
    // the only diagonal that stays closed. Holding Ctrl flips which way the staircase turns first
    // (Alt removes paths and Shift shows vegetation, so both are taken). A hint at the top of the
    // screen tells the player.
    //
    // UICore.ReadyPlaceEnd is one long coroutine; its drag code (a) snaps the mouse to one axis,
    // (b) takes the tile count from the drag length and (c) computes every tile as
    // m_placeStart + direction * k, both for the preview and for the final placement. The
    // transpiler (a) undoes the snap for diagonal drags, (b) sets the count to the staircase
    // length and (c) routes direction * k through Step(), which returns staircase tile k for a
    // diagonal direction and the normal result for anything else.
    public static class DiagonalBuildPatch
    {

        private static readonly AccessTools.FieldRef<UICore, Place> HoldingPlace =
            AccessTools.FieldRefAccess<UICore, Place>("m_holdingPlace");
        private static readonly AccessTools.FieldRef<UICore, Vector3> PlaceStart =
            AccessTools.FieldRefAccess<UICore, Vector3>("m_placeStart");

        private static bool s_active;
        private static readonly List<Vector3> Stairs = new List<Vector3>();

        [HarmonyPatch]
        public static class ReadyPlaceEnd
        {
            public static MethodBase TargetMethod() =>
                AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(UICore), "ReadyPlaceEnd"));

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var original = instructions.ToList();
                try
                {
                    return Rewrite(original);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Diagonal building not available: {e.Message}");
                    return original;
                }
            }
        }

        private static IEnumerable<CodeInstruction> Rewrite(List<CodeInstruction> instructions)
        {
            FieldInfo placeStart = AccessTools.Field(typeof(UICore), "m_placeStart");
            MethodInfo subtract = AccessTools.Method(typeof(Vector3), "op_Subtraction");
            MethodInfo magnitude = AccessTools.PropertyGetter(typeof(Vector3), nameof(Vector3.magnitude));
            MethodInfo multiply = AccessTools.Method(typeof(Vector3), "op_Multiply", new[] { typeof(Vector3), typeof(float) });

            var matcher = new CodeMatcher(instructions);

            // (a) Right after the snap: "Vector3 vector2 = mouse - m_placeStart" (locals: 1 = UICore,
            // 2 = mouse on grid, 3 = m_placeStart - mouse before snapping)
            matcher.MatchStartForward(
                new CodeMatch(OpCodes.Ldloc_2),
                new CodeMatch(OpCodes.Ldloc_1),
                new CodeMatch(OpCodes.Ldfld, placeStart),
                new CodeMatch(OpCodes.Call, subtract));
            if (matcher.IsInvalid)
                throw new InvalidOperationException("snap location not found");
            var labels = matcher.Instruction.ExtractLabels();
            matcher.Insert(
                new CodeInstruction(OpCodes.Ldloca_S, (byte)2).WithLabels(labels),
                new CodeInstruction(OpCodes.Ldloc_3),
                new CodeInstruction(OpCodes.Ldloc_1),
                CodeInstruction.Call(typeof(DiagonalBuildPatch), nameof(Unsnap)));

            // (b) "int num = (int)vector2.magnitude" (local 7)
            matcher.MatchEndForward(
                new CodeMatch(OpCodes.Call, magnitude),
                new CodeMatch(OpCodes.Conv_I4),
                new CodeMatch(ci => ci.IsStloc()));
            if (matcher.IsInvalid)
                throw new InvalidOperationException("tile count not found");
            var storeCount = matcher.Instruction;
            matcher.Advance(1).Insert(
                new CodeInstruction(storeCount.opcode == OpCodes.Stloc_S ? OpCodes.Ldloc_S : OpCodes.Ldloc, storeCount.operand),
                CodeInstruction.Call(typeof(DiagonalBuildPatch), nameof(Count)),
                new CodeInstruction(storeCount.opcode, storeCount.operand));

            // (c) every direction * k
            int replaced = 0;
            foreach (CodeInstruction ci in matcher.InstructionEnumeration())
            {
                if (ci.Calls(multiply))
                {
                    ci.operand = AccessTools.Method(typeof(DiagonalBuildPatch), nameof(Step));
                    replaced++;
                }
            }
            Plugin.Log.LogInfo($"Diagonal building ready ({replaced} tile calculations)");
            return matcher.InstructionEnumeration();
        }

        // Undoes the axis snap for a diagonal fence/path drag and prepares the staircase
        public static void Unsnap(ref Vector3 mouse, Vector3 startMinusRawMouse, UICore ui)
        {
            s_active = false;
            Place place = HoldingPlace(ui);
            if (!ModConfig.DiagonalBuilding.Value || place == null ||
                (place.m_placeType != Place.PlaceType.Connector && place.m_placeType != Place.PlaceType.Path))
                return;

            int dx = -Mathf.RoundToInt(startMinusRawMouse.x);
            int dz = -Mathf.RoundToInt(startMinusRawMouse.z);
            int a = Mathf.Abs(dx), b = Mathf.Abs(dz);
            // Minimum angle away from straight before a drag counts as diagonal
            float ratio = Mathf.Tan(ModConfig.StraightSnapAngle.Value * Mathf.Deg2Rad);
            if (a == 0 || b == 0 || Mathf.Min(a, b) < ratio * Mathf.Max(a, b))
                return;

            Vector3 start = PlaceStart(ui);
            mouse.x = start.x + dx;
            mouse.z = start.z + dz;
            BuildStairs(dx, dz, UnityEngine.InputSystem.Keyboard.current?.ctrlKey.isPressed == true);
            s_active = true;
        }

        public static int Count(int straightCount) => s_active ? Stairs.Count - 1 : straightCount;

        // Replacement for Vector3 * float inside ReadyPlaceEnd
        public static Vector3 Step(Vector3 direction, float k)
        {
            if (!s_active || Mathf.Abs(direction.x) < 0.01f || Mathf.Abs(direction.z) < 0.01f)
                return direction * k;
            int i = Mathf.Clamp(Mathf.RoundToInt(k), 0, Stairs.Count - 1);
            return Stairs[i];
        }

        // 4-connected line from (0,0) to (dx,dz) that stays as close to the straight line as possible.
        // Flipped: the same staircase built from the other end, so it turns the other way first.
        private static void BuildStairs(int dx, int dz, bool flipped)
        {
            BuildStairs(flipped ? -dx : dx, flipped ? -dz : dz);
            if (!flipped)
                return;
            Stairs.Reverse();
            var end = new Vector3(dx, 0f, dz);
            for (int i = 0; i < Stairs.Count; i++)
                Stairs[i] += end;
        }

        private static void BuildStairs(int dx, int dz)
        {
            Stairs.Clear();
            int a = Mathf.Abs(dx), b = Mathf.Abs(dz);
            int sx = Math.Sign(dx), sz = Math.Sign(dz);
            int x = 0, z = 0;
            Stairs.Add(Vector3.zero);
            while (x < a || z < b)
            {
                bool stepX = z >= b || (x < a && Math.Abs((x + 1) * b - z * a) <= Math.Abs(x * b - (z + 1) * a));
                if (stepX)
                    x++;
                else
                    z++;
                Stairs.Add(new Vector3(x * sx, 0f, z * sz));
            }
        }
    }
}
