using System.Globalization;
using HarmonyLib;
using ModuleWheels;
using UnityEngine;
using VehiclePhysics;

namespace com.github.lhervier.ksp.fix.landedshape
{
    /// <summary>The spring and damper a wheel suspension pushes with, at each of the three levels that hold them.</summary>
    internal struct WheelState
    {
        /// <summary>What ModuleWheelSuspension computed last (its private spring and damper), and whether it has sized
        /// its multipliers automatically already.</summary>
        public float KspSpring;
        public float KspDamper;
        public bool AutoSet;

        /// <summary>What the VehiclePhysics wheel collider hands to Unity's at its next step.</summary>
        public float VpSpring;
        public float VpDamper;

        /// <summary>What Unity's WheelCollider pushes with: spring, damper, and the compression it rests at.</summary>
        public float USpring;
        public float UDamper;
        public float UTarget;

        /// <summary>Whether the wheel touched the ground.</summary>
        public bool Grounded;
    }

    /// <summary>
    /// Takes and gives back the state of a wheel suspension that a save loses.
    ///
    /// The game saves a suspension's multipliers, not the spring and damper they gave: those start from nothing at
    /// load. ModuleWheelSuspension only sizes them again once its part is unpacked and its wheel reports touching the
    /// ground, one step at least after the unpack, and Unity's WheelCollider takes them from the VehiclePhysics one a
    /// step later still, its rest compression moving towards its value by a small amount per step. Meanwhile the
    /// vessel stands on springs of almost nothing and sinks into them. Given back as soon as the game builds the wheel
    /// (<see cref="WheelSetupPatch"/>), the suspensions push from their first step as they pushed when it was saved.
    /// </summary>
    internal static class SuspensionState
    {
        private static readonly AccessTools.FieldRef<ModuleWheelSuspension, float> Spring =
            AccessTools.FieldRefAccess<ModuleWheelSuspension, float>("spring");
        private static readonly AccessTools.FieldRef<ModuleWheelSuspension, float> Damper =
            AccessTools.FieldRefAccess<ModuleWheelSuspension, float>("damper");
        private static readonly AccessTools.FieldRef<ModuleWheelSuspension, bool> AutoSpringDamperSet =
            AccessTools.FieldRefAccess<ModuleWheelSuspension, bool>("autoSpringDamperSet");

        /// <summary>Takes the suspension state of <paramref name="part"/>; false when it has no wheel suspension set up.</summary>
        public static bool TryCapture(Part part, out WheelState state)
        {
            state = default(WheelState);
            ModuleWheelSuspension suspension;
            VPWheelCollider vp;
            WheelCollider unity;
            if (!Find(part, out suspension, out vp, out unity))
            {
                return false;
            }
            state.KspSpring = Spring(suspension);
            state.KspDamper = Damper(suspension);
            state.AutoSet = AutoSpringDamperSet(suspension);
            state.VpSpring = vp.springRate;
            state.VpDamper = vp.damperRate;
            JointSpring spring = unity.suspensionSpring;
            state.USpring = spring.spring;
            state.UDamper = spring.damper;
            state.UTarget = spring.targetPosition;
            state.Grounded = vp != null && FindWheel(part) != null && FindWheel(part).IsGrounded;
            return true;
        }

        /// <summary>Gives <paramref name="part"/>'s wheel suspension back the state taken; false when it has none.</summary>
        public static bool Restore(Part part, WheelState state)
        {
            ModuleWheelSuspension suspension;
            VPWheelCollider vp;
            WheelCollider unity;
            if (!Find(part, out suspension, out vp, out unity))
            {
                return false;
            }
            Spring(suspension) = state.KspSpring;
            Damper(suspension) = state.KspDamper;
            AutoSpringDamperSet(suspension) = state.AutoSet;
            vp.springRate = state.VpSpring;
            vp.damperRate = state.VpDamper;
            vp.runtimeSpringRate = state.VpSpring;
            vp.runtimeDamperRate = state.VpDamper;
            unity.suspensionSpring = new JointSpring
            {
                spring = state.USpring,
                damper = state.UDamper,
                targetPosition = state.UTarget
            };
            return true;
        }

        private static bool Find(Part part, out ModuleWheelSuspension suspension, out VPWheelCollider vp,
            out WheelCollider unity)
        {
            vp = null;
            unity = null;
            suspension = part.FindModuleImplementing<ModuleWheelSuspension>();
            ModuleWheelBase wheelBase = part.FindModuleImplementing<ModuleWheelBase>();
            if (suspension == null || wheelBase == null || wheelBase.Wheel == null)
            {
                return false;
            }
            vp = wheelBase.Wheel.wheelCollider;
            unity = vp != null ? vp.GetWheelCollider() : null;
            return unity != null;
        }

        private static KSPWheelController FindWheel(Part part)
        {
            ModuleWheelBase wheelBase = part.FindModuleImplementing<ModuleWheelBase>();
            return wheelBase != null ? wheelBase.Wheel : null;
        }

        /// <summary>The state as written in a save: nine values, comma-separated.</summary>
        public static string Format(WheelState s)
        {
            return string.Join(",", F(s.KspSpring), F(s.KspDamper), s.AutoSet ? "1" : "0", F(s.VpSpring), F(s.VpDamper),
                F(s.USpring), F(s.UDamper), F(s.UTarget), s.Grounded ? "1" : "0");
        }

        /// <summary>Reads what <see cref="Format"/> wrote; false when it cannot.</summary>
        public static bool TryParse(string value, out WheelState state)
        {
            state = default(WheelState);
            if (value == null)
            {
                return false;
            }
            // Eight values before the grounded flag was saved: such a wheel is taken as on the ground.
            string[] items = value.Split(',');
            if (items.Length != 8 && items.Length != 9)
            {
                return false;
            }
            float[] f = new float[9];
            f[8] = 1f;
            for (int i = 0; i < items.Length; i++)
            {
                if (!float.TryParse(items[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i]))
                {
                    return false;
                }
            }
            state.KspSpring = f[0];
            state.KspDamper = f[1];
            state.AutoSet = f[2] != 0f;
            state.VpSpring = f[3];
            state.VpDamper = f[4];
            state.USpring = f[5];
            state.UDamper = f[6];
            state.UTarget = f[7];
            state.Grounded = f[8] != 0f;
            return true;
        }

        private static string F(float x)
        {
            return x.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
