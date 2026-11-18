using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace com.github.lhervier.ksp.fix.landedshape
{
    /// <summary>
    /// The pose and velocity of a part, and of its servo's moving part if it has one, in the frame of the vessel's
    /// root; and the state of its wheel suspension if it has one.
    /// </summary>
    internal struct PartPose
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
        public bool HasServo;
        public Vector3 ServoPosition;
        public Quaternion ServoRotation;
        public Vector3 ServoVelocity;
        public Vector3 ServoAngularVelocity;
        public bool HasWheel;
        public WheelState Wheel;
    }

    /// <summary>
    /// Keeps the shape and motion of a landed vessel across saves and rails: the pose and velocity of each part
    /// relative to the root, taken when the vessel is saved unpacked or goes on rails, written with the vessel, and put
    /// back when it leaves the rails.
    ///
    /// Stock saves each part at its construction pose (orgPos, orgRot), never where its load bent it to, and puts it
    /// back there on every move while the vessel is packed. A heavy vessel is therefore loaded straight: its joints
    /// bend again as physics starts, and the parts pushed into the ground (wheels at the end of their travel, tanks
    /// resting on the ground) are thrown out of it. The attach joints are created while the vessel is packed, on the
    /// construction shape, so their rest is that shape: putting the bent shape back once the vessel is unpacked,
    /// before the first physics step, gives the joints back the strain they had. The autostruts, created one step
    /// later, aim at the construction shape when KSPCF's AutoStrutDrift is there. Stock also starts every part at rest:
    /// a load swinging under a crane when the vessel was saved swings again from where it was, with the velocity it
    /// had.
    ///
    /// KSP then eases the vessel into physics (VesselPrecalculate.StartEasing): its weight comes back over 16 steps,
    /// from nothing. The joints put back under the strain of the full weight would relax meanwhile, and the vessel
    /// would bounce when its weight comes back: that easing is skipped (<see cref="NoEasingAfterShape"/>). The wheel
    /// suspensions get back the spring and damper they pushed with (<see cref="SuspensionState"/>), and the vessel is
    /// held still until its wheels have found the ground (<see cref="Hold"/>).
    ///
    /// The velocities saved are those of the motion the game shows, measured from the poses of the last physics steps
    /// (<see cref="SampleMotion"/>), not Rigidbody.velocity: PhysX moves a body with the velocity it has after its
    /// position iterations, then keeps the one it gets after its velocity iterations, which never converges on a light
    /// part held in a stiff chain of heavy ones (a 20 kg docking port reads 0.3 m/s while it does not move).
    /// </summary>
    public class LandedShapeModule : VesselModule
    {
        private const string PartNodeName = "PART";

        // Closer than this to its construction pose, in metres and degrees, and slower than this, in metres and
        // radians per second, a part's shape is not saved: the physics leaves fractions of a millimetre everywhere.
        private const float MinOffset = 0.0001f;
        private const float MinAngle = 0.01f;
        private const float MinSpeed = 0.001f;
        private const float MinSpin = 0.001f;

        /// <summary>The shape to put back at the next unpack, by part persistentId; null when there is none.</summary>
        private Dictionary<uint, PartPose> pending;

        /// <summary>The root's own velocity and angular velocity, in its frame, that go with <see cref="pending"/>.</summary>
        private Vector3 pendingRootVelocity;
        private Vector3 pendingRootAngularVelocity;

        // How long after the shape is put back, in seconds of physics time, the easing it brings is skipped.
        private const float EasingWindow = 1f;

        /// <summary>When the shape was last put back (Time.fixedTime), or a negative value.</summary>
        private float restoredAt = -1f;

        /// <summary>
        /// The suspension states put back with the shape, by part persistentId, waiting for the game to build their
        /// wheels; null when none waits.
        /// </summary>
        private Dictionary<uint, WheelState> pendingWheels;

        // How many physics steps at most the vessel is held still for its wheels to find the ground.
        private const int MaxHoldSteps = 25;

        /// <summary>The bodies held kinematic since the shape was put back, until the wheels touch the ground; null when
        /// none is held.</summary>
        private List<Rigidbody> held;

        /// <summary>The parts whose wheel touched the ground when the vessel was saved, and must touch it again before
        /// the vessel is let go.</summary>
        private List<Part> heldWheels;

        private int heldSteps;

        /// <summary>The shape being put back, kept until the vessel is let go, for its velocities.</summary>
        private Dictionary<uint, PartPose> moving;
        private Vector3 movingRootVelocity;
        private Vector3 movingRootAngularVelocity;

        // How many physics steps a velocity is measured over. Positions are floats: over a single step, a vessel 2 km
        // from the floating origin would read up to 12 mm/s of rounding; over 5 steps (0.1 s), 2.4 mm/s, still far
        // shorter than a load swinging.
        private const int MotionSteps = 5;
        private const int MotionSlots = MotionSteps + 1;

        /// <summary>The poses of one body at the last physics steps, in a ring indexed like <see cref="sampleTimes"/>.</summary>
        private sealed class BodyTrack
        {
            public Rigidbody Body;
            public readonly Vector3[] Positions = new Vector3[MotionSlots];
            public readonly Quaternion[] Rotations = new Quaternion[MotionSlots];
        }

        /// <summary>The bodies whose motion is sampled, by body; null when the vessel is not sampled.</summary>
        private Dictionary<Rigidbody, BodyTrack> tracks;

        /// <summary>The part count of the vessel when <see cref="tracks"/> was built.</summary>
        private int trackedParts;

        private readonly float[] sampleTimes = new float[MotionSlots];
        private int sampleCount;
        private int newestSample;

        /// <summary>
        /// Whether the shape has just been put back, within <see cref="EasingWindow"/>; forgets it, so that only the
        /// easing that follows the unpack is skipped.
        /// </summary>
        internal bool TakeJustRestored()
        {
            bool just = restoredAt >= 0f && Time.fixedTime - restoredAt < EasingWindow;
            restoredAt = -1f;
            return just;
        }

        protected override void OnStart()
        {
            if (Settings.KeepLandedShape)
            {
                GameEvents.onVesselGoOffRails.Add(OnVesselGoOffRails);
                GameEvents.onFloatingOriginShift.Add(OnFloatingOriginShift);
                GameEvents.onVesselWasModified.Add(OnVesselWasModified);
            }
        }

        private void OnDestroy()
        {
            GameEvents.onVesselGoOffRails.Remove(OnVesselGoOffRails);
            GameEvents.onFloatingOriginShift.Remove(OnFloatingOriginShift);
            GameEvents.onVesselWasModified.Remove(OnVesselWasModified);
        }

        // A shift moves every body between two samples: their motion is measured again from there.
        private void OnFloatingOriginShift(Vector3d offset, Vector3d nonKrakensbane)
        {
            ForgetMotion();
        }

        private void OnVesselWasModified(Vessel v)
        {
            if (v == vessel)
            {
                ForgetMotion();
            }
        }

        protected override void OnLoad(ConfigNode node)
        {
            pending = Read(node);
            float[] v = Parse(node.GetValue("rootVel"), 3);
            float[] w = Parse(node.GetValue("rootAngVel"), 3);
            pendingRootVelocity = v != null ? new Vector3(v[0], v[1], v[2]) : Vector3.zero;
            pendingRootAngularVelocity = w != null ? new Vector3(w[0], w[1], w[2]) : Vector3.zero;
            if (pending != null)
            {
                Log.Debug($"{Name()}: shape of {pending.Count} part(s) loaded");
            }
        }

        protected override void OnSave(ConfigNode node)
        {
            if (!Settings.KeepLandedShape)
            {
                return;
            }
            // Unpacked, the shape is the live one; packed, the parts sit at their construction pose, so the shape is
            // the one taken before (at the last GoOnRails, or loaded and not put back yet).
            Dictionary<uint, PartPose> shape = pending;
            Vector3 rootVelocity = pendingRootVelocity;
            Vector3 rootAngularVelocity = pendingRootAngularVelocity;
            if (vessel != null && vessel.loaded && !vessel.packed)
            {
                if (held != null && moving != null)
                {
                    // Held still since it was put back: the shape and velocities are still the ones loaded.
                    shape = moving;
                    rootVelocity = movingRootVelocity;
                    rootAngularVelocity = movingRootAngularVelocity;
                }
                else
                {
                    shape = vessel.Landed ? Capture(out rootVelocity, out rootAngularVelocity) : null;
                }
            }
            if (shape == null)
            {
                return;
            }
            node.AddValue("rootVel", Format(rootVelocity));
            node.AddValue("rootAngVel", Format(rootAngularVelocity));
            Write(node, shape);
        }

        public override void OnGoOnRails()
        {
            bool stillHeld = held != null && moving != null;
            Dictionary<uint, PartPose> loaded = moving;
            pendingWheels = null;
            held = null;
            heldWheels = null;
            moving = null;
            // Called by Vessel.GoOnRails before the parts are packed: they are still where the physics left them.
            if (!Settings.KeepLandedShape || vessel == null || !vessel.loaded || vessel.packed)
            {
                ForgetMotion();
                return;
            }
            if (stillHeld)
            {
                // Held still since it was put back: the shape and velocities are still the ones loaded.
                pending = loaded;
                pendingRootVelocity = movingRootVelocity;
                pendingRootAngularVelocity = movingRootAngularVelocity;
            }
            else
            {
                pending = vessel.Landed ? Capture(out pendingRootVelocity, out pendingRootAngularVelocity) : null;
            }
            ForgetMotion();
            if (pending != null)
            {
                Log.Debug($"{Name()}: going on rails, shape of {pending.Count} part(s) kept");
            }
        }

        private void OnVesselGoOffRails(Vessel v)
        {
            if (v != vessel || pending == null)
            {
                return;
            }
            Dictionary<uint, PartPose> shape = pending;
            pending = null;
            if (!vessel.Landed)
            {
                Log.Debug($"{Name()}: not landed when unpacked, shape dropped");
                return;
            }
            Restore(shape);
        }

        /// <summary>
        /// Returns the pose and velocity of each physical part away from its construction pose, or moving, or with a
        /// servo or a wheel suspension, and the state of that suspension; and the root's own velocities.
        /// </summary>
        private Dictionary<uint, PartPose> Capture(out Vector3 rootVelocity, out Vector3 rootAngularVelocity)
        {
            Part rootPart = vessel.rootPart;
            Transform root = rootPart.transform;
            Quaternion toRoot = Quaternion.Inverse(root.rotation);
            VelocityReport report = new VelocityReport();
            Vector3 v, w;
            if (rootPart.rb != null)
            {
                VelocityOf(rootPart.rb, rootPart, ref report, out v, out w);
            }
            else
            {
                v = w = Vector3.zero;
            }
            rootVelocity = toRoot * v;
            rootAngularVelocity = toRoot * w;
            Dictionary<uint, PartPose> shape = new Dictionary<uint, PartPose>();
            foreach (Part part in vessel.parts)
            {
                if (part == rootPart || part.rb == null || part.physicalSignificance != Part.PhysicalSignificance.FULL)
                {
                    continue;
                }
                VelocityOf(part.rb, part, ref report, out v, out w);
                PartPose pose = new PartPose
                {
                    Position = toRoot * (part.transform.position - root.position),
                    Rotation = toRoot * part.transform.rotation,
                    Velocity = toRoot * v,
                    AngularVelocity = toRoot * w
                };
                if (part.servoRb != null)
                {
                    VelocityOf(part.servoRb, part, ref report, out v, out w);
                    pose.HasServo = true;
                    pose.ServoPosition = toRoot * (part.servoRb.transform.position - root.position);
                    pose.ServoRotation = toRoot * part.servoRb.transform.rotation;
                    pose.ServoVelocity = toRoot * v;
                    pose.ServoAngularVelocity = toRoot * w;
                }
                pose.HasWheel = SuspensionState.TryCapture(part, out pose.Wheel);
                bool moved = (pose.Position - part.orgPos).magnitude >= MinOffset ||
                    Quaternion.Angle(pose.Rotation, part.orgRot) >= MinAngle;
                bool moving = pose.Velocity.magnitude >= MinSpeed || pose.AngularVelocity.magnitude >= MinSpin;
                if (moved || moving || pose.HasServo || pose.HasWheel)
                {
                    shape[part.persistentId] = pose;
                }
            }
            Log.Debug($"{Name()}: velocities of {report.Measured} body(ies) measured from their motion over " +
                $"{report.Steps} step(s), {report.Read} read from PhysX; largest gap with the PhysX velocity " +
                $"{report.LargestGap * 1000f:0.0} mm/s on {report.LargestGapPart ?? "-"}");
            return shape;
        }

        /// <summary>How the velocities of a capture were obtained, for the log.</summary>
        private struct VelocityReport
        {
            public int Measured;
            public int Read;
            public int Steps;
            public float LargestGap;
            public string LargestGapPart;
        }

        /// <summary>
        /// The world velocity and angular velocity of <paramref name="body"/> (of <paramref name="part"/>): measured
        /// from its motion over the last physics steps, or its PhysX velocity when it has not been sampled long enough.
        /// </summary>
        private void VelocityOf(Rigidbody body, Part part, ref VelocityReport report, out Vector3 velocity,
            out Vector3 angularVelocity)
        {
            int steps;
            if (!TryMeasureMotion(body, out velocity, out angularVelocity, out steps))
            {
                velocity = body.velocity;
                angularVelocity = body.angularVelocity;
                report.Read++;
                return;
            }
            report.Measured++;
            report.Steps = steps;
            float gap = (velocity - body.velocity).magnitude;
            if (gap > report.LargestGap || report.LargestGapPart == null)
            {
                report.LargestGap = gap;
                report.LargestGapPart = $"{part.partInfo.name} ({part.persistentId}){(body == part.servoRb ? " servo" : "")}";
            }
        }

        /// <summary>Forgets the sampled motion: it is measured again from the next physics step.</summary>
        private void ForgetMotion()
        {
            tracks = null;
            sampleCount = 0;
        }

        /// <summary>
        /// Records the pose of each physical body of the vessel, at each physics step, while it is loaded, unpacked,
        /// landed and not held; forgets them otherwise.
        /// </summary>
        private void SampleMotion()
        {
            if (!Settings.KeepLandedShape || vessel == null || !vessel.loaded || vessel.packed || !vessel.Landed ||
                held != null)
            {
                if (tracks != null)
                {
                    ForgetMotion();
                }
                return;
            }
            if (tracks == null || trackedParts != vessel.parts.Count)
            {
                tracks = new Dictionary<Rigidbody, BodyTrack>();
                foreach (Part p in vessel.parts)
                {
                    if (p.rb != null && p.physicalSignificance == Part.PhysicalSignificance.FULL)
                    {
                        tracks[p.rb] = new BodyTrack { Body = p.rb };
                    }
                    if (p.servoRb != null)
                    {
                        tracks[p.servoRb] = new BodyTrack { Body = p.servoRb };
                    }
                }
                trackedParts = vessel.parts.Count;
                sampleCount = 0;
                newestSample = MotionSlots - 1;
            }
            int slot = (newestSample + 1) % MotionSlots;
            foreach (BodyTrack track in tracks.Values)
            {
                if (track.Body == null)
                {
                    // A body destroyed without the vessel being reported modified.
                    ForgetMotion();
                    return;
                }
                track.Positions[slot] = track.Body.position;
                track.Rotations[slot] = track.Body.rotation;
            }
            sampleTimes[slot] = Time.fixedTime;
            newestSample = slot;
            sampleCount = Mathf.Min(sampleCount + 1, MotionSlots);
        }

        /// <summary>
        /// The world velocity and angular velocity of <paramref name="body"/> between its oldest and newest sampled
        /// poses, over <paramref name="steps"/> physics steps; false when it has not been sampled at two steps.
        /// </summary>
        private bool TryMeasureMotion(Rigidbody body, out Vector3 velocity, out Vector3 angularVelocity, out int steps)
        {
            velocity = angularVelocity = Vector3.zero;
            steps = sampleCount - 1;
            BodyTrack track;
            if (tracks == null || steps < 1 || !tracks.TryGetValue(body, out track))
            {
                return false;
            }
            int oldest = (newestSample - steps + MotionSlots) % MotionSlots;
            float dt = sampleTimes[newestSample] - sampleTimes[oldest];
            if (dt <= 0f)
            {
                return false;
            }
            velocity = (track.Positions[newestSample] - track.Positions[oldest]) / dt;
            Quaternion turn = track.Rotations[newestSample] * Quaternion.Inverse(track.Rotations[oldest]);
            angularVelocity = RotationVector(turn) / dt;
            return true;
        }

        /// <summary>The rotation <paramref name="q"/> as a vector: its axis times its angle in radians, the short way.</summary>
        private static Vector3 RotationVector(Quaternion q)
        {
            if (q.w < 0f)
            {
                q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            }
            Vector3 axis = new Vector3(q.x, q.y, q.z);
            float sin = axis.magnitude;
            if (sin < 1e-7f)
            {
                return 2f * axis;
            }
            return axis * (2f * Mathf.Atan2(sin, q.w) / sin);
        }

        /// <summary>
        /// Moves each part of <paramref name="shape"/> to its pose relative to the root, and either holds the vessel
        /// still until its wheels touch the ground, or gives the parts their velocities at once.
        /// </summary>
        private void Restore(Dictionary<uint, PartPose> shape)
        {
            ForgetMotion();
            Transform root = vessel.rootPart.transform;
            Vector3 rootPosition = root.position;
            Quaternion rootRotation = root.rotation;
            int restored = 0;
            int wheels = 0;
            float maxMove = 0f;
            string maxPart = "-";
            foreach (Part part in vessel.parts)
            {
                PartPose pose;
                if (!shape.TryGetValue(part.persistentId, out pose))
                {
                    continue;
                }
                if (part.rb == null || part.physicalSignificance != Part.PhysicalSignificance.FULL)
                {
                    continue;
                }
                Vector3 position = rootPosition + rootRotation * pose.Position;
                Quaternion rotation = rootRotation * pose.Rotation;
                float move = (position - part.transform.position).magnitude;
                float angle = Quaternion.Angle(rotation, part.transform.rotation);
                part.transform.SetPositionAndRotation(position, rotation);
                part.rb.position = position;
                part.rb.rotation = rotation;
                if (pose.HasServo && part.servoRb != null)
                {
                    Vector3 servoPosition = rootPosition + rootRotation * pose.ServoPosition;
                    Quaternion servoRotation = rootRotation * pose.ServoRotation;
                    part.servoRb.transform.SetPositionAndRotation(servoPosition, servoRotation);
                    part.servoRb.position = servoPosition;
                    part.servoRb.rotation = servoRotation;
                }
                if (pose.HasWheel)
                {
                    // The game builds the wheel later (see OnWheelBuilt).
                    if (pendingWheels == null)
                    {
                        pendingWheels = new Dictionary<uint, WheelState>();
                    }
                    pendingWheels[part.persistentId] = pose.Wheel;
                    wheels++;
                }
                restored++;
                if (move > maxMove)
                {
                    maxMove = move;
                    maxPart = $"{part.partInfo.name} ({part.persistentId})";
                }
                Log.Debug($"{Name()}: {part.partInfo.name} ({part.persistentId}) put back {move * 1000f:0.0} mm, " +
                    $"{angle:0.000}°{(pose.HasServo ? ", with its servo" : "")}" +
                    (pose.HasWheel ? $", suspension spring {pose.Wheel.USpring:0.#}, damper {pose.Wheel.UDamper:0.#}, " +
                        $"rest compression {pose.Wheel.UTarget:0.###}" : ""));
            }
            Physics.SyncTransforms();
            restoredAt = Time.fixedTime;
            moving = shape;
            movingRootVelocity = pendingRootVelocity;
            movingRootAngularVelocity = pendingRootAngularVelocity;
            Log.Info($"{Name()}: shape put back on {restored} of {shape.Count} saved part(s), largest move " +
                $"{maxMove * 1000f:0.0} mm on {maxPart}; {wheels} wheel suspension(s) waiting for their wheel");
            if (wheels > 0)
            {
                Hold(shape);
            }
            else
            {
                SetVelocities();
            }
        }

        /// <summary>
        /// Called once the game has built the wheel of <paramref name="part"/>: gives its suspension back the state
        /// put back with the shape, and keeps its part held if the vessel is.
        /// </summary>
        internal void OnWheelBuilt(Part part)
        {
            WheelState state;
            if (pendingWheels == null || !pendingWheels.TryGetValue(part.persistentId, out state))
            {
                return;
            }
            pendingWheels.Remove(part.persistentId);
            if (pendingWheels.Count == 0)
            {
                pendingWheels = null;
            }
            bool given = SuspensionState.Restore(part, state);
            // Building the wheel makes its part's body dynamic again.
            if (held != null && part.rb != null && !part.rb.isKinematic)
            {
                part.rb.isKinematic = true;
                held.Add(part.rb);
            }
            Log.Debug($"{Name()}: {part.partInfo.name} ({part.persistentId}) " +
                (given ? $"suspension given back spring {state.USpring:0.#}, damper {state.UDamper:0.#}, rest " +
                    $"compression {state.UTarget:0.###}" : "suspension not found, left as the game sets it"));
        }

        /// <summary>
        /// Holds every body of the vessel still (kinematic) in the shape put back, until each wheel of
        /// <paramref name="shape"/> that touched the ground when the vessel was saved touches it again (see
        /// <see cref="FixedUpdate"/>).
        ///
        /// A wheel finds the ground only after a physics step: until then, its suspension pushes on nothing, and its
        /// part's joints, put back under the strain of the vessel's weight, throw it upwards; the structure bends by
        /// centimetres in that step, and the ground pushes it back at the next one.
        /// </summary>
        private void Hold(Dictionary<uint, PartPose> shape)
        {
            held = new List<Rigidbody>();
            heldWheels = new List<Part>();
            heldSteps = 0;
            foreach (Part p in vessel.parts)
            {
                if (p.rb != null && !p.rb.isKinematic)
                {
                    p.rb.isKinematic = true;
                    held.Add(p.rb);
                }
                if (p.servoRb != null && !p.servoRb.isKinematic)
                {
                    p.servoRb.isKinematic = true;
                    held.Add(p.servoRb);
                }
                PartPose pose;
                if (shape.TryGetValue(p.persistentId, out pose) && pose.HasWheel && pose.Wheel.Grounded)
                {
                    heldWheels.Add(p);
                }
            }
        }

        /// <summary>
        /// Samples the motion of the vessel (<see cref="SampleMotion"/>); lets the held vessel go, with its saved
        /// velocities, once every wheel that touched the ground touches it again, or after <see cref="MaxHoldSteps"/>.
        /// </summary>
        private void FixedUpdate()
        {
            // Before letting go: the step that lets go is sampled from the next one on.
            SampleMotion();
            if (held == null)
            {
                return;
            }
            heldSteps++;
            int grounded = 0;
            foreach (Part p in heldWheels)
            {
                // A wheel not built yet, or whose state waits to be given back, is not ready; a part with no wheel
                // module at all never will be.
                ModuleWheelBase wheelBase = p != null ? p.FindModuleImplementing<ModuleWheelBase>() : null;
                bool waiting = p != null && pendingWheels != null && pendingWheels.ContainsKey(p.persistentId);
                if (wheelBase == null || (!waiting && wheelBase.Wheel != null && wheelBase.Wheel.IsGrounded))
                {
                    grounded++;
                }
            }
            if (grounded < heldWheels.Count && heldSteps < MaxHoldSteps)
            {
                return;
            }
            foreach (Rigidbody rb in held)
            {
                if (rb != null)
                {
                    rb.isKinematic = false;
                }
            }
            SetVelocities();
            Log.Info($"{Name()}: let go after {heldSteps} physics step(s) held, {grounded} of {heldWheels.Count} wheel(s) " +
                "on the ground");
            held = null;
            heldWheels = null;
        }

        /// <summary>
        /// Gives every part the velocity it had when the shape was taken: its saved one, or rest for a part slow enough
        /// not to be saved.
        /// </summary>
        private void SetVelocities()
        {
            if (moving == null)
            {
                return;
            }
            Quaternion rootRotation = vessel.rootPart.transform.rotation;
            float fastest = 0f;
            foreach (Part p in vessel.parts)
            {
                if (p.rb == null || p.rb.isKinematic)
                {
                    continue;
                }
                PartPose pose;
                bool saved = moving.TryGetValue(p.persistentId, out pose);
                if (p == vessel.rootPart)
                {
                    p.rb.velocity = rootRotation * movingRootVelocity;
                    p.rb.angularVelocity = rootRotation * movingRootAngularVelocity;
                }
                else
                {
                    p.rb.velocity = saved ? rootRotation * pose.Velocity : Vector3.zero;
                    p.rb.angularVelocity = saved ? rootRotation * pose.AngularVelocity : Vector3.zero;
                }
                fastest = Mathf.Max(fastest, p.rb.velocity.magnitude);
                if (p.servoRb != null && !p.servoRb.isKinematic)
                {
                    p.servoRb.velocity = saved && pose.HasServo ? rootRotation * pose.ServoVelocity : Vector3.zero;
                    p.servoRb.angularVelocity = saved && pose.HasServo ? rootRotation * pose.ServoAngularVelocity : Vector3.zero;
                }
            }
            moving = null;
            Log.Debug($"{Name()}: parts given back their velocities, fastest {fastest * 1000f:0.0} mm/s");
        }

        private string Name()
        {
            return vessel != null ? vessel.GetDisplayName() : "?";
        }

        private static void Write(ConfigNode node, Dictionary<uint, PartPose> shape)
        {
            foreach (KeyValuePair<uint, PartPose> entry in shape)
            {
                PartPose pose = entry.Value;
                ConfigNode partNode = node.AddNode(PartNodeName);
                partNode.AddValue("id", entry.Key.ToString(CultureInfo.InvariantCulture));
                partNode.AddValue("pos", Format(pose.Position));
                partNode.AddValue("rot", Format(pose.Rotation));
                partNode.AddValue("vel", Format(pose.Velocity));
                partNode.AddValue("angVel", Format(pose.AngularVelocity));
                if (pose.HasServo)
                {
                    partNode.AddValue("servoPos", Format(pose.ServoPosition));
                    partNode.AddValue("servoRot", Format(pose.ServoRotation));
                    partNode.AddValue("servoVel", Format(pose.ServoVelocity));
                    partNode.AddValue("servoAngVel", Format(pose.ServoAngularVelocity));
                }
                if (pose.HasWheel)
                {
                    partNode.AddValue("wheel", SuspensionState.Format(pose.Wheel));
                }
            }
        }

        private static Dictionary<uint, PartPose> Read(ConfigNode node)
        {
            ConfigNode[] partNodes = node.GetNodes(PartNodeName);
            if (partNodes.Length == 0)
            {
                return null;
            }
            Dictionary<uint, PartPose> shape = new Dictionary<uint, PartPose>();
            foreach (ConfigNode partNode in partNodes)
            {
                uint id;
                float[] pos = Parse(partNode.GetValue("pos"), 3);
                float[] rot = Parse(partNode.GetValue("rot"), 4);
                if (!uint.TryParse(partNode.GetValue("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) ||
                    pos == null || rot == null)
                {
                    Log.Warning("Unreadable part shape, skipped: " + partNode);
                    continue;
                }
                PartPose pose = new PartPose
                {
                    Position = new Vector3(pos[0], pos[1], pos[2]),
                    Rotation = new Quaternion(rot[0], rot[1], rot[2], rot[3]),
                    Velocity = ReadVector(partNode, "vel"),
                    AngularVelocity = ReadVector(partNode, "angVel")
                };
                float[] servoPos = Parse(partNode.GetValue("servoPos"), 3);
                float[] servoRot = Parse(partNode.GetValue("servoRot"), 4);
                if (servoPos != null && servoRot != null)
                {
                    pose.HasServo = true;
                    pose.ServoPosition = new Vector3(servoPos[0], servoPos[1], servoPos[2]);
                    pose.ServoRotation = new Quaternion(servoRot[0], servoRot[1], servoRot[2], servoRot[3]);
                    pose.ServoVelocity = ReadVector(partNode, "servoVel");
                    pose.ServoAngularVelocity = ReadVector(partNode, "servoAngVel");
                }
                pose.HasWheel = SuspensionState.TryParse(partNode.GetValue("wheel"), out pose.Wheel);
                shape[id] = pose;
            }
            return shape;
        }

        /// <summary>The vector value <paramref name="name"/> of <paramref name="node"/>, or zero when it is missing.</summary>
        private static Vector3 ReadVector(ConfigNode node, string name)
        {
            float[] v = Parse(node.GetValue(name), 3);
            return v != null ? new Vector3(v[0], v[1], v[2]) : Vector3.zero;
        }

        private static string Format(Vector3 v)
        {
            return string.Join(",", v.x.ToString("R", CultureInfo.InvariantCulture), v.y.ToString("R", CultureInfo.InvariantCulture),
                v.z.ToString("R", CultureInfo.InvariantCulture));
        }

        private static string Format(Quaternion q)
        {
            return string.Join(",", q.x.ToString("R", CultureInfo.InvariantCulture), q.y.ToString("R", CultureInfo.InvariantCulture),
                q.z.ToString("R", CultureInfo.InvariantCulture), q.w.ToString("R", CultureInfo.InvariantCulture));
        }

        private static float[] Parse(string value, int count)
        {
            if (value == null)
            {
                return null;
            }
            string[] items = value.Split(',');
            if (items.Length != count)
            {
                return null;
            }
            float[] result = new float[count];
            for (int i = 0; i < count; i++)
            {
                if (!float.TryParse(items[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                {
                    return null;
                }
            }
            return result;
        }
    }
}
