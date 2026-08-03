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

        void Start()
        {
            Vector3 euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        }

        void Update()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || !mouse.rightButton.isPressed)
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

            Vector3 move = Vector3.zero;
            if (keyboard.wKey.isPressed) move += transform.forward;
            if (keyboard.sKey.isPressed) move -= transform.forward;
            if (keyboard.dKey.isPressed) move += transform.right;
            if (keyboard.aKey.isPressed) move -= transform.right;

            if (move.sqrMagnitude > 1f)
                move.Normalize();

            transform.position += move * (MoveSpeed * Time.deltaTime);
        }
    }
}