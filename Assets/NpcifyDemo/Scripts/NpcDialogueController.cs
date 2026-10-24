using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Models;
using StarterAssets;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NpcifyDemo
{
    /// <summary>
    /// Finds the NPC near the player, shows the interaction prompt, and runs the
    /// conversation: speech with typewriter text, then reply choices.
    /// </summary>
    public sealed class NpcDialogueController : MonoBehaviour
    {
        private const string LeaveLabel = "Leave";
        private const string RetryLabel = "Try again";

        private enum Phase
        {
            Exploring,
            Waiting,
            Speaking,
            Choosing,
        }

        [SerializeField] private Transform player;
        [SerializeField] private Transform playerLookTarget;
        [SerializeField] private DialogueView view;
        [SerializeField] private DialogueCameraRig dialogueCamera;
        [SerializeField] private float interactDistance = 2.6f;

        private readonly List<NpcifyNpc> _npcs = new List<NpcifyNpc>();
        private readonly List<string> _choiceLabels = new List<string>();

        private StarterAssetsInputs _inputs;
        private PlayerInput _playerInput;
        private NpcifyNpc _nearby;
        private NpcifyNpc _active;
        private Phase _phase = Phase.Exploring;
        private StagingResultPayload _staging;
        private CancellationTokenSource _conversation;
        private string _pendingRetryMessage;
        private bool _ending;

        public NpcifyNpc ActiveNpc => _active;
        public NpcifyNpc NearbyNpc => _nearby;
        public bool InConversation => _active != null;
        public bool IsChoosing => _phase == Phase.Choosing;
        public IReadOnlyList<string> ChoiceLabels => _choiceLabels;

        private void Start()
        {
            _inputs = player.GetComponent<StarterAssetsInputs>();
            _playerInput = player.GetComponent<PlayerInput>();
            _npcs.AddRange(FindObjectsByType<NpcifyNpc>(FindObjectsSortMode.None));
            foreach (var npc in _npcs)
            {
                view.RegisterNpc(npc);
            }
            view.PromptClicked += TryOpenNearby;
            view.ChoiceClicked += Choose;
            view.CustomReplySubmitted += message => _ = SendReplyAsync(message);
            view.BodyClicked += Advance;
            view.CloseClicked += Close;
        }

        private void Update()
        {
            view.UpdateNpcLabels(player.position, _active);
            if (_active == null)
            {
                UpdateExploring();
            }
            else
            {
                UpdateConversation();
            }
        }

        public void TryOpenNearby()
        {
            if (_active == null && _nearby != null && _nearby.IsReady)
            {
                Open(_nearby);
            }
        }

        public void Choose(int index)
        {
            if (_phase != Phase.Choosing || index < 0 || index >= _choiceLabels.Count)
            {
                return;
            }
            var label = _choiceLabels[index];
            if (label == LeaveLabel)
            {
                Close();
                return;
            }
            if (label == RetryLabel)
            {
                var retry = _pendingRetryMessage;
                _ = retry == null ? BeginAsync() : SendReplyAsync(retry);
                return;
            }
            var option = _staging?.Options != null && index < _staging.Options.Count ? _staging.Options[index] : null;
            _ending = option != null && option.End;
            _ = SendReplyAsync(label);
        }

        public void Close()
        {
            if (_active == null)
            {
                return;
            }
            _conversation?.Cancel();
            _conversation?.Dispose();
            _conversation = null;
            _active.Character.OnSpeechEnd -= HandleSpeechEnd;
            _active.EndConversation();
            _active = null;
            _staging = null;
            _phase = Phase.Exploring;
            view.HideDialogue();
            dialogueCamera.Release();
            SetPlayerControl(true);
        }

        private void UpdateExploring()
        {
            _nearby = FindNearbyNpc();
            if (_nearby == null)
            {
                view.HidePrompt();
                return;
            }

            var interactable = _nearby.IsReady;
            var text = interactable ? $"<b>[E]</b>  Talk to {_nearby.DisplayName}" : _nearby.StatusMessage;
            view.ShowPrompt(_nearby, text, interactable);

            var keyboard = Keyboard.current;
            var gamepad = Gamepad.current;
            var mouse = Mouse.current;
            var pressed = (keyboard != null && keyboard.eKey.wasPressedThisFrame) ||
                          (gamepad != null && gamepad.buttonWest.wasPressedThisFrame) ||
                          (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState == CursorLockMode.Locked);
            if (pressed)
            {
                TryOpenNearby();
            }
        }

        private void UpdateConversation()
        {
            FacePlayerTowards(_active.transform.position);
            var character = _active.Character;

            switch (_phase)
            {
                case Phase.Waiting:
                    var dots = new string('.', 1 + (int)(Time.time * 2.5f) % 3);
                    view.SetBody($"<i><color=#B9BECB>{_active.DisplayName} is thinking{dots}</color></i>");
                    break;
                case Phase.Speaking:
                    var revealed = character.Speech.RevealedText;
                    view.SetBody(string.IsNullOrEmpty(revealed) ? CurrentBlockText() : revealed);
                    break;
            }

            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }
            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                Close();
                return;
            }
            if (view.IsReplyFieldFocused)
            {
                return;
            }
            if (keyboard.spaceKey.wasPressedThisFrame)
            {
                Advance();
            }
            if (keyboard.tabKey.wasPressedThisFrame)
            {
                view.FocusReplyField();
            }
            for (var i = 0; i < 9; i++)
            {
                if (keyboard[Key.Digit1 + i].wasPressedThisFrame || keyboard[Key.Numpad1 + i].wasPressedThisFrame)
                {
                    Choose(i);
                }
            }
        }

        private void Open(NpcifyNpc npc)
        {
            _active = npc;
            _ending = false;
            view.HidePrompt();
            view.ShowDialogue(npc.DisplayName, npc.ModeLabel, npc.AllowCustomReply);
            dialogueCamera.Focus(player, npc);
            npc.FaceTowards(playerLookTarget != null ? playerLookTarget : player);
            npc.Character.OnSpeechEnd += HandleSpeechEnd;
            SetPlayerControl(false);
            _ = BeginAsync();
        }

        private async Task BeginAsync()
        {
            var npc = _active;
            var token = RestartConversationToken();
            EnterWaiting();
            try
            {
                var staging = await npc.BeginConversationAsync(token);
                if (npc == _active && !token.IsCancellationRequested)
                {
                    EnterSpeaking(staging);
                }
            }
            catch (OperationCanceledException)
            {
                // The player left the conversation.
            }
            catch (Exception ex)
            {
                ShowError(npc, ex, null);
            }
        }

        private async Task SendReplyAsync(string message)
        {
            if (_active == null || (_phase != Phase.Choosing && !_active.AllowCustomReply))
            {
                return;
            }
            var npc = _active;
            var token = RestartConversationToken();
            EnterWaiting();
            try
            {
                var staging = await npc.ReplyAsync(message, token);
                if (npc != _active || token.IsCancellationRequested)
                {
                    return;
                }
                if (staging == null)
                {
                    Close();
                    return;
                }
                EnterSpeaking(staging);
            }
            catch (OperationCanceledException)
            {
                // The player left the conversation.
            }
            catch (Exception ex)
            {
                ShowError(npc, ex, message);
            }
        }

        private void Advance()
        {
            if (_phase != Phase.Speaking || _active == null)
            {
                return;
            }
            _active.Character.StopSpeaking();
            EnterChoosing();
        }

        private void HandleSpeechEnd(StagingResultPayload staging)
        {
            if (_active != null && _phase == Phase.Speaking && staging == _staging)
            {
                EnterChoosing();
            }
        }

        private void EnterWaiting()
        {
            _phase = Phase.Waiting;
            view.SetChoices(Array.Empty<string>());
            view.SetReplyInteractable(false);
            view.SetHint("Esc  Leave");
        }

        private void EnterSpeaking(StagingResultPayload staging)
        {
            _staging = staging;
            _phase = Phase.Speaking;
            view.SetHint("Space  Skip    Esc  Leave");
            if (!_active.Character.Speech.Speaking)
            {
                // A staging without speech completes at once.
                EnterChoosing();
            }
        }

        private void EnterChoosing()
        {
            _phase = Phase.Choosing;
            view.SetBody(CurrentStagingText());
            _choiceLabels.Clear();
            var options = _staging?.Options;
            if (!_ending && options != null)
            {
                foreach (var option in options)
                {
                    if (!string.IsNullOrWhiteSpace(option.Label))
                    {
                        _choiceLabels.Add(option.Label);
                    }
                }
            }
            if (_choiceLabels.Count == 0)
            {
                _choiceLabels.Add(LeaveLabel);
            }
            view.SetChoices(_choiceLabels);
            view.SetReplyInteractable(!_ending);
            view.SetHint(_active.AllowCustomReply ? "1-9  Choose    Tab  Type    Esc  Leave" : "1-9  Choose    Esc  Leave");
        }

        private void ShowError(NpcifyNpc npc, Exception ex, string retryMessage)
        {
            if (npc != _active)
            {
                return;
            }
            Debug.LogWarning($"[Npcify demo] {npc.DisplayName}: {ex.Message}");
            _phase = Phase.Choosing;
            _pendingRetryMessage = retryMessage;
            view.SetBody($"<color=#FF9C8A>The conversation stopped: {ex.Message}</color>");
            _choiceLabels.Clear();
            _choiceLabels.Add(RetryLabel);
            _choiceLabels.Add(LeaveLabel);
            view.SetChoices(_choiceLabels);
            view.SetReplyInteractable(false);
        }

        private string CurrentBlockText()
        {
            return _active?.Character.Speech.CurrentText ?? string.Empty;
        }

        private string CurrentStagingText()
        {
            var dialogue = _staging?.Timeline?.Dialogue;
            if (dialogue != null && dialogue.Count > 0)
            {
                return dialogue[dialogue.Count - 1].Text;
            }
            return _staging?.Text ?? string.Empty;
        }

        private CancellationToken RestartConversationToken()
        {
            _conversation?.Cancel();
            _conversation?.Dispose();
            _conversation = new CancellationTokenSource();
            return _conversation.Token;
        }

        private NpcifyNpc FindNearbyNpc()
        {
            NpcifyNpc best = null;
            var bestDistance = interactDistance;
            foreach (var npc in _npcs)
            {
                var offset = npc.transform.position - player.position;
                offset.y = 0f;
                var distance = offset.magnitude;
                if (distance < bestDistance)
                {
                    best = npc;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private void FacePlayerTowards(Vector3 target)
        {
            var flat = target - player.position;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f)
            {
                return;
            }
            var desired = Quaternion.LookRotation(flat.normalized, Vector3.up);
            player.rotation = Quaternion.RotateTowards(player.rotation, desired, 360f * Time.deltaTime);
        }

        private void SetPlayerControl(bool enabled)
        {
            if (_inputs != null)
            {
                _inputs.move = Vector2.zero;
                _inputs.look = Vector2.zero;
                _inputs.jump = false;
                _inputs.sprint = false;
                _inputs.cursorLocked = enabled;
                _inputs.cursorInputForLook = enabled;
            }
            if (_playerInput != null)
            {
                if (enabled)
                {
                    _playerInput.ActivateInput();
                }
                else
                {
                    _playerInput.DeactivateInput();
                }
            }
            Cursor.lockState = enabled ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !enabled;
        }
    }
}
