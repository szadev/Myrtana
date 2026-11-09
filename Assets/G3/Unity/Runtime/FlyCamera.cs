using UnityEngine;
using UnityEngine.InputSystem;

namespace G3.Unity
{
    /// <summary>
    /// Free-fly camera for exploring imported regions in Play mode.
    /// Hold right mouse to look, WASD to move, Q/E down/up, Shift ×5 speed, Ctrl ×0.2, mouse wheel changes base speed.
    /// [ / ] change time of day on a G3Environment in the scene (if present via SendMessage-free reflection).
    /// </summary>
    public sealed class FlyCamera : MonoBehaviour
    {
        public float speed = 10f;
        public float lookSensitivity = 0.15f;
        float _yaw, _pitch;

        void OnEnable()
        {
            var e = transform.eulerAngles;
            _yaw = e.y;
            _pitch = e.x > 180 ? e.x - 360 : e.x;
        }

        void Update()
        {
            var kb = global::G3.Unity.G3Input.GameKeyboard;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            if (mouse.rightButton.isPressed)
            {
                var d = mouse.delta.ReadValue() * lookSensitivity;
                _yaw += d.x;
                _pitch = Mathf.Clamp(_pitch - d.y, -89f, 89f);
                transform.rotation = Quaternion.Euler(_pitch, _yaw, 0);
            }

            float scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0) speed = Mathf.Clamp(speed * (scroll > 0 ? 1.2f : 1 / 1.2f), 1f, 500f);

            var move = Vector3.zero;
            if (kb.wKey.isPressed) move += Vector3.forward;
            if (kb.sKey.isPressed) move += Vector3.back;
            if (kb.aKey.isPressed) move += Vector3.left;
            if (kb.dKey.isPressed) move += Vector3.right;
            if (kb.eKey.isPressed) move += Vector3.up;
            if (kb.qKey.isPressed) move += Vector3.down;
            float s = speed * (kb.leftShiftKey.isPressed ? 5f : 1f) * (kb.leftCtrlKey.isPressed ? 0.2f : 1f);
            transform.Translate(move * s * Time.unscaledDeltaTime, Space.Self);
        }
    }
}
