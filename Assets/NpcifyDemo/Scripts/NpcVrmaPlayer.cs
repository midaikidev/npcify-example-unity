using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UniGLTF;
using UnityEngine;
using UniVRM10;

namespace NpcifyDemo
{
    /// <summary>
    /// Plays the character's VRMA animations on a runtime-loaded VRM 1.0 model.
    /// The idle animation loops. Other animations play once and then return to idle.
    /// </summary>
    public sealed class NpcVrmaPlayer : MonoBehaviour
    {
        private readonly Dictionary<string, Vrm10AnimationInstance> _loaded =
            new Dictionary<string, Vrm10AnimationInstance>(StringComparer.Ordinal);
        private readonly Dictionary<string, Task<Vrm10AnimationInstance>> _loading =
            new Dictionary<string, Task<Vrm10AnimationInstance>>(StringComparer.Ordinal);

        private Vrm10Instance _vrm;
        private Func<string, Task<byte[]>> _loadBytes;
        private string _idleId;
        private string _currentId;
        private float _returnToIdleAt = -1f;

        public string CurrentAnimationId => _currentId;

        public void Initialize(Vrm10Instance vrm, string idleAnimationId, Func<string, Task<byte[]>> loadBytes)
        {
            _vrm = vrm;
            _idleId = idleAnimationId;
            _loadBytes = loadBytes;
        }

        public async Task PlayIdleAsync()
        {
            if (string.IsNullOrEmpty(_idleId))
            {
                return;
            }
            await PlayAsync(_idleId, true);
        }

        public async Task PlayAsync(string animationId, bool loop)
        {
            if (_vrm == null || string.IsNullOrWhiteSpace(animationId))
            {
                return;
            }

            Vrm10AnimationInstance instance;
            try
            {
                instance = await GetOrLoadAsync(animationId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Npcify demo] Animation '{animationId}' is not available: {ex.Message}");
                return;
            }
            if (instance == null || _vrm == null)
            {
                return;
            }

            var isIdle = animationId == _idleId;
            var shouldLoop = loop || isIdle;
            var legacy = instance.GetComponent<Animation>();
            if (legacy != null && legacy.clip != null)
            {
                legacy.clip.wrapMode = shouldLoop ? WrapMode.Loop : WrapMode.ClampForever;
                legacy.wrapMode = legacy.clip.wrapMode;
                legacy.Stop();
                legacy.Play(legacy.clip.name);
                _returnToIdleAt = shouldLoop ? -1f : Time.time + legacy.clip.length;
            }

            _vrm.Runtime.VrmAnimation = instance;
            _currentId = animationId;
        }

        private void Update()
        {
            if (_returnToIdleAt > 0f && Time.time >= _returnToIdleAt)
            {
                _returnToIdleAt = -1f;
                _ = PlayIdleAsync();
            }
        }

        private Task<Vrm10AnimationInstance> GetOrLoadAsync(string animationId)
        {
            if (_loaded.TryGetValue(animationId, out var cached))
            {
                return Task.FromResult(cached);
            }
            if (!_loading.TryGetValue(animationId, out var pending))
            {
                pending = LoadAsync(animationId);
                _loading[animationId] = pending;
            }
            return pending;
        }

        private async Task<Vrm10AnimationInstance> LoadAsync(string animationId)
        {
            try
            {
                var bytes = await _loadBytes(animationId);
                using var data = new GlbBinaryParser(bytes, animationId + ".vrma").Parse();
                using var loader = new VrmAnimationImporter(new VrmAnimationData(data));
                var gltfInstance = await loader.LoadAsync(new RuntimeOnlyAwaitCaller());
                var animation = gltfInstance.GetComponent<Vrm10AnimationInstance>();
                animation.ShowBoxMan(false);
                gltfInstance.transform.SetParent(transform, false);
                _loaded[animationId] = animation;
                return animation;
            }
            finally
            {
                _loading.Remove(animationId);
            }
        }

        private void OnDestroy()
        {
            foreach (var instance in _loaded.Values)
            {
                if (instance != null)
                {
                    Destroy(instance.gameObject);
                }
            }
            _loaded.Clear();
        }
    }
}
