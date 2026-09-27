using System.Collections.Generic;
using HeroGame.Core.Audio;
using HeroGame.Core.Foundation;
using HeroGame.Runtime.Bootstrap;
using UnityEngine;

namespace HeroGame.Runtime.Audio
{
    /// <summary>
    /// Plays the world (GDD Phase 23): the core <see cref="SoundscapeMixer"/> says what should be audible where the
    /// camera is — district ambience, night, rain, wind, thunder, crowds, sirens of units really on calls — and this
    /// fades looping layers toward those volumes. Also the one-shot entry points for UI, phone, powers and voices.
    /// All sounds are procedural placeholders unless recorded clips exist under Resources/Audio (ASSET_TRACKER).
    /// </summary>
    public sealed class AudioDirector : MonoBehaviour
    {
        private static AudioDirector _instance;

        private AudioSource _bedA, _bedB, _night, _rain, _wind, _crowd;
        private bool _aActive = true;
        private Synth.AmbienceKind _bed = (Synth.AmbienceKind)(-1);
        private readonly List<AudioSource> _sirens = new List<AudioSource>();
        private AudioSource _oneShots;
        private Soundscape _target;
        private float _nextMix;
        private ulong _thunderCount;
        private bool _subscribed;

        private void Awake()
        {
            _instance = this;
            _bedA = Loop("Bed A");
            _bedB = Loop("Bed B");
            _night = Loop("Night", ProceduralClips.Prefer("amb_night", "amb:night", () => Synth.Ambience(Synth.AmbienceKind.Night)));
            _rain = Loop("Rain", ProceduralClips.Prefer("rain", "rain", () => Synth.Rain(0.8f)));
            _wind = Loop("Wind", ProceduralClips.Prefer("wind", "wind", Synth.Wind));
            _crowd = Loop("Crowd", ProceduralClips.Prefer("crowd", "amb:city", () => Synth.Ambience(Synth.AmbienceKind.City)));
            _crowd.pitch = 1.3f;
            for (var i = 0; i < 3; i++)
            {
                var s = Loop("Siren " + i);
                s.spatialBlend = 1f;
                s.rolloffMode = AudioRolloffMode.Linear;
                s.maxDistance = SoundscapeMixer.SirenRange;
                _sirens.Add(s);
            }
            _oneShots = gameObject.AddComponent<AudioSource>();
            _oneShots.playOnAwake = false;
        }

        private AudioSource Loop(string name, AudioClip clip = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.loop = true;
            s.playOnAwake = false;
            s.volume = 0f;
            s.spatialBlend = 0f;
            if (clip != null)
            {
                s.clip = clip;
                s.Play();
            }
            return s;
        }

        private void Update()
        {
            if (!ServiceRegistry.TryGet<GameSession>(out var session)) return;
            if (!_subscribed)
            {
                session.World.Phone.MessageReceived += (to, msg) =>
                {
                    if (session.LocalCharacter != null && to == session.LocalCharacter) Ui(Synth.UiSound.Notification);
                };
                _subscribed = true;
            }
            var cam = Camera.main;
            if (cam == null) return;
            var listener = cam.transform.position;
            if (Time.unscaledTime >= _nextMix)
            {
                _nextMix = Time.unscaledTime + 0.5f;
                var indoors = Physics.Raycast(listener, Vector3.up, 25f);
                _target = SoundscapeMixer.Mix(session.World, new WorldPosition(listener.x, listener.y, listener.z), indoors);
                if (_target.Bed != _bed) SwitchBed(_target.Bed);
                if (_target.ThunderPerSecond > 0f && Random.value < _target.ThunderPerSecond * 0.5f)
                {
                    var seed = ++_thunderCount % 4;
                    _oneShots.PlayOneShot(ProceduralClips.Get("thunder:" + seed, () => Synth.Thunder(seed)), AudioVolumes.Effective(AudioChannel.Ambience));
                }
                for (var i = 0; i < _sirens.Count; i++)
                {
                    var s = _sirens[i];
                    if (i < _target.Sirens.Count)
                    {
                        var src = _target.Sirens[i];
                        var clip = ProceduralClips.Prefer("siren_" + src.Kind, "siren:" + src.Kind, () => Synth.Siren(src.Kind));
                        if (s.clip != clip) { s.clip = clip; s.Play(); }
                        s.transform.position = new Vector3(src.Position.X, src.Position.Y + 1.5f, src.Position.Z);
                        s.volume = AudioVolumes.Effective(AudioChannel.Effects);
                    }
                    else if (s.isPlaying) s.Stop();
                }
            }
            if (_target == null) return;
            var amb = AudioVolumes.Effective(AudioChannel.Ambience);
            var k = Time.unscaledDeltaTime * 0.8f;
            var active = _aActive ? _bedA : _bedB;
            var idle = _aActive ? _bedB : _bedA;
            active.volume = Mathf.MoveTowards(active.volume, _target.BedVolume * amb, k);
            idle.volume = Mathf.MoveTowards(idle.volume, 0f, k);
            _night.volume = Mathf.MoveTowards(_night.volume, _target.NightVolume * amb, k);
            _rain.volume = Mathf.MoveTowards(_rain.volume, _target.RainVolume * amb, k);
            _wind.volume = Mathf.MoveTowards(_wind.volume, _target.WindVolume * amb, k);
            _crowd.volume = Mathf.MoveTowards(_crowd.volume, _target.CrowdVolume * amb, k);
        }

        private void SwitchBed(Synth.AmbienceKind bed)
        {
            _bed = bed;
            _aActive = !_aActive;
            var next = _aActive ? _bedA : _bedB;
            next.clip = ProceduralClips.Prefer("amb_" + bed, "amb:" + bed, () => Synth.Ambience(bed));
            next.volume = 0f;
            next.Play();
        }

        // ------------------------------------------------------------------ one-shots

        public static void Ui(Synth.UiSound sound)
        {
            if (_instance == null) return;
            _instance._oneShots.PlayOneShot(ProceduralClips.Prefer("ui_" + sound, "ui:" + sound, () => Synth.Ui(sound)), AudioVolumes.Effective(AudioChannel.Interface));
        }

        public static void PowerCharge(string element, Vector3 at) =>
            PlayAt(ProceduralClips.Prefer("power_charge_" + element, "charge:" + element, () => Synth.PowerCharge(element)), at, AudioChannel.Effects);

        public static void PowerImpact(string element, Vector3 at) =>
            PlayAt(ProceduralClips.Prefer("power_impact_" + element, "impact:" + element, () => Synth.PowerImpact(element)), at, AudioChannel.Effects);

        /// <summary>A line of dialogue: the placeholder voice is a babble in the speaker's own pitch; subtitles carry the words.</summary>
        public static void Voice(string speaker, string text)
        {
            if (_instance == null || string.IsNullOrEmpty(speaker) || string.IsNullOrEmpty(text)) return;
            var seed = StableHash.Of(speaker);
            var key = "voice:" + seed + ":" + StableHash.Of(text);
            _instance._oneShots.PlayOneShot(ProceduralClips.Get(key, () => Synth.Babble(seed, text)), AudioVolumes.Effective(AudioChannel.Voice));
        }

        public static void PlayAt(AudioClip clip, Vector3 at, AudioChannel channel)
        {
            if (clip == null) return;
            AudioSource.PlayClipAtPoint(clip, at, AudioVolumes.Effective(channel));
        }
    }
}
