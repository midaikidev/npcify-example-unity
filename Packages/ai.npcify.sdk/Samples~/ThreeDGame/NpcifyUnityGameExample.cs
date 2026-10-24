#if UNITY_5_3_OR_NEWER
using Npcify.Sdk.Unity;
using UnityEngine;

namespace Npcify.Sdk.Samples
{
    public sealed class NpcifyUnityGameExample : MonoBehaviour
    {
        [SerializeField] private NpcifyCharacter character;
        [SerializeField] private Transform playerCamera;
        [SerializeField] private Transform weaponPrefab;

        private async void Start()
        {
            if (character == null)
            {
                character = GetComponent<NpcifyCharacter>();
            }

            character.RefreshAvatarBindings();
            character.GazeTarget = playerCamera;

            // Attach an in-game prop to the humanoid right hand bone
            var rightHand = character.GetBone(HumanBodyBones.RightHand);
            if (rightHand != null && weaponPrefab != null)
            {
                Instantiate(weaponPrefab, rightHand, false);
            }

            // Inspect real-time SkinnedMeshRenderers and 3D AudioSource
            Debug.Log($"Loaded {character.SkinnedMeshRenderers.Count} skinned meshes on {character.Root.name}");
            character.AudioSource.maxDistance = 18f;

            // Subscribe to live subtitle reveal events
            character.OnCharacterRevealed += (revealedText, cue) =>
            {
                Debug.Log($"Subtitle: {revealedText}");
            };

            character.SetEmotion("happy", 0.6f);
            await character.SpeakAsync("Welcome to the realm, traveler! How can I assist you?");
        }

        private void Update()
        {
            if (character == null)
            {
                return;
            }

            // Read real-time speech and mouth openness every frame in your game loop
            var openness = character.Expressions.MouthOpenness;
            var speaking = character.Speech.Speaking;
            if (speaking && openness > 0.5f)
            {
                // Drive custom game shaders, voice indicators, or gameplay triggers
            }
        }
    }
}
#endif
