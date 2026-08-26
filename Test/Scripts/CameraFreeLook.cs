using UnityEngine;
using UnityEngine.InputSystem;

namespace Monospark
{
    // Unity Scene-view-style free camera: hold right mouse button to look
    // around (yaw/pitch) and move with WASD, in full 3D relative to that look
    // direction (not locked to the horizontal plane — this is for flying
    // around/through a room-scale volumetric dataset, not walking on a
    // floor). Release right-click and it's inert, so the cursor stays free
    // for clicking TestUI's OnGUI buttons without any lock/unlock step.
    public class CameraFreeLook : MonoBehaviour
    {
        public float MoveSpeed = 5f;
        public float LookSensitivity = 0.1f;
        public float MaxPitch = 89f;

        float _yaw;
        float _pitch;
        
        public bool MoveHorizontally = false;
        public float PhysicsRadius = 0.3f;
        public LayerMask WallLayer;
        
        
        void Start()
        {
            Vector3 euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        }

        void Update()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.isPressed)
                return;

            UpdateLook(mouse);
            UpdateMove(Keyboard.current);
        }

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