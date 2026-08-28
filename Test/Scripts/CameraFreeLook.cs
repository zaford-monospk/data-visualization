using UnityEngine;
using UnityEngine.InputSystem;

namespace Monospark
{
    // Unity Scene-view-style free camera: looks around (yaw/pitch) and moves
    // with WASD, in full 3D relative to that look direction (not locked to
    // the horizontal plane — this is for flying around/through a room-scale
    // volumetric dataset, not walking on a floor). Which mouse input actually
    // triggers looking is configurable (see LookTrigger) -- by default it's
    // LeftClick, so releasing it leaves the camera inert and the cursor free
    // for clicking TestUI's OnGUI buttons without any lock/unlock step; that
    // stops being true under Always (see LookTrigger's own doc comment).
    public class CameraFreeLook : MonoBehaviour
    {
        // Which mouse input has to be active for the camera to look around
        // (and, since WASD move is gated by the same condition, to move too).
        public enum LookTriggerMode
        {
            Always,     // no button needed -- looks around continuously.
                        // NOTE: this also captures the cursor for TestUI's
                        // OnGUI buttons, unlike the two click-gated modes.
            LeftClick,  // hold left mouse button (the original/default behavior).
            RightClick  // hold right mouse button.
        }

        public float MoveSpeed = 5f;
        public float LookSensitivity = 0.1f;
        public float MaxPitch = 89f;

        float _yaw;
        float _pitch;

        public bool MoveHorizontally = false;
        public bool WASDMoveEnabled = true;
        public LookTriggerMode LookTrigger = LookTriggerMode.LeftClick;
        public float PhysicsRadius = 0.3f;
        public LayerMask WallLayer;

        // Sets which mouse input triggers looking around -- e.g. wire to a
        // dropdown/set of UI Buttons (each passing its own LookTriggerMode)
        // so a tester can switch between Always/LeftClick/RightClick without
        // touching the Inspector.
        public void SetLookTrigger(LookTriggerMode trigger)
        {
            LookTrigger = trigger;
        }

        // Flips MoveHorizontally -- switches between full-3D fly (move
        // relative to look direction, including up/down) and floor-locked
        // horizontal movement. Parameterless so it can be wired straight to
        // a UI Button's OnClick (e.g. from TestUI) or a hotkey binding.
        public void ToggleHorizontalMove()
        {
            MoveHorizontally = !MoveHorizontally;
        }

        // Flips whether WASD moves the camera at all -- mouse-look still
        // works either way (this only gates UpdateMove, not the whole
        // component), so a tester can freeze camera movement without losing
        // the ability to look around.
        public void ToggleWASDMove()
        {
            WASDMoveEnabled = !WASDMoveEnabled;
        }

        void Start()
        {
            Vector3 euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        }

        void Update()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || !IsLookTriggered(mouse))
                return;

            UpdateLook(mouse);
            if (WASDMoveEnabled)
                UpdateMove(Keyboard.current);
        }

        bool IsLookTriggered(Mouse mouse) => LookTrigger switch
        {
            LookTriggerMode.Always => true,
            LookTriggerMode.RightClick => mouse.rightButton.isPressed,
            _ => mouse.leftButton.isPressed // LeftClick, and the safe default for an unhandled enum value
        };

        void UpdateLook(Mouse mouse)
        {
            Vector2 delta = mouse.delta.ReadValue();
            _yaw += delta.x * LookSensitivity;
            _pitch -= delta.y * LookSensitivity;
            _pitch = Mathf.Clamp(_pitch, -MaxPitch, MaxPitch);

            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        void UpdateMove(Keyboard keyboard)
        {
            if (keyboard == null)
                return;

            Vector3 forward = transform.forward;
            Vector3 right = transform.right;
            if (MoveHorizontally)
            {
                forward.y = 0f;
                right.y = 0f;
                forward.Normalize();
                right.Normalize();
            }

            Vector3 move = Vector3.zero;
            if (keyboard.wKey.isPressed) move += forward;
            if (keyboard.sKey.isPressed) move -= forward;
            if (keyboard.dKey.isPressed) move += right;
            if (keyboard.aKey.isPressed) move -= right;

            if (move.sqrMagnitude > 1f)
                move.Normalize();

            Vector3 delta = move * (MoveSpeed * Time.deltaTime);
            transform.position += ResolveMove(transform.position, delta);
        }

        // Sphere-casts the intended delta against WallLayer and, on a hit,
        // slides the leftover movement along the surface instead of just
        // stopping — so grazing a wall at an angle slides around it rather
        // than snagging.
        Vector3 ResolveMove(Vector3 origin, Vector3 delta)
        {
            const int maxIterations = 3;
            const float skin = 0.01f;

            Vector3 position = origin;
            Vector3 remaining = delta;

            for (int i = 0; i < maxIterations && remaining.sqrMagnitude > 0.0000001f; i++)
            {
                float distance = remaining.magnitude;
                Vector3 direction = remaining / distance;

                if (Physics.SphereCast(position, PhysicsRadius, direction, out RaycastHit hit, distance, WallLayer, QueryTriggerInteraction.Ignore))
                {
                    float safeDistance = Mathf.Max(hit.distance - skin, 0f);
                    position += direction * safeDistance;

                    Vector3 leftover = direction * (distance - safeDistance);
                    remaining = Vector3.ProjectOnPlane(leftover, hit.normal);
                }
                else
                {
                    position += remaining;
                    remaining = Vector3.zero;
                }
            }

            return position - origin;
        }
    }
}