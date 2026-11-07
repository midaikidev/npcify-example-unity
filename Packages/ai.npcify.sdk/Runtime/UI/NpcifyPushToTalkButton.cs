#if UNITY_5_3_OR_NEWER && NPCIFY_UGUI
using UnityEngine;
using UnityEngine.EventSystems;

namespace Npcify.Sdk.Unity
{
    /// <summary>
    /// Press and hold a UI element (a Button or any Graphic) to talk to an NpcifyCharacter
    /// (docs/voice-input.md). The release sends the speech; a press shorter than 0.3 s sends
    /// nothing. Needs the Unity UI package (com.unity.ugui).
    /// </summary>
    [AddComponentMenu("Npcify/Npcify Push To Talk Button")]
    [DisallowMultipleComponent]
    public sealed class NpcifyPushToTalkButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        [SerializeField] private NpcifyCharacter character;

        public NpcifyCharacter Character
        {
            get => character;
            set => character = value;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (character != null && eventData.button == PointerEventData.InputButton.Left)
            {
                character.BeginPushToTalk();
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (character != null && eventData.button == PointerEventData.InputButton.Left)
            {
                character.EndPushToTalk();
            }
        }

        private void OnDisable()
        {
            if (character != null)
            {
                character.CancelPushToTalk();
            }
        }
    }
}
#endif
