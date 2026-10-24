using Unity.Cinemachine;
using UnityEngine;

namespace NpcifyDemo
{
    /// <summary>
    /// Over-the-shoulder conversation shot. The camera stays behind the player's right
    /// shoulder and frames the NPC's face. Cinemachine blends to it and back.
    /// </summary>
    [RequireComponent(typeof(CinemachineCamera))]
    public sealed class DialogueCameraRig : MonoBehaviour
    {
        [SerializeField] private float distanceBehindPlayer = 2.1f;
        [SerializeField] private float shoulderOffset = 1.05f;
        [SerializeField] private float heightOffset = 0.18f;

        private CinemachineCamera _camera;
        private Transform _player;
        private NpcifyNpc _npc;

        private void Awake()
        {
            _camera = GetComponent<CinemachineCamera>();
            _camera.Priority = 100;
            gameObject.SetActive(false);
        }

        public void Focus(Transform player, NpcifyNpc npc)
        {
            _player = player;
            _npc = npc;
            UpdatePose();
            gameObject.SetActive(true);
        }

        public void Release()
        {
            _npc = null;
            gameObject.SetActive(false);
        }

        private void LateUpdate()
        {
            UpdatePose();
        }

        private void UpdatePose()
        {
            if (_player == null || _npc == null)
            {
                return;
            }

            var focus = _npc.FocusPosition;
            var playerEyes = _player.position + Vector3.up * (focus.y - _npc.transform.position.y);
            var toNpc = focus - playerEyes;
            toNpc.y = 0f;
            if (toNpc.sqrMagnitude < 0.0001f)
            {
                toNpc = _player.forward;
            }
            toNpc.Normalize();
            var right = Vector3.Cross(Vector3.up, toNpc);

            var position = playerEyes - toNpc * distanceBehindPlayer + right * shoulderOffset + Vector3.up * heightOffset;
            transform.SetPositionAndRotation(position, Quaternion.LookRotation(focus - position, Vector3.up));
        }
    }
}
