using HeroGame.Runtime.Bootstrap;
using UnityEngine;

namespace HeroGame.Runtime.Presentation
{
    /// <summary>
    /// Drives the sun from the authoritative world clock (never from local time), so every client on a
    /// server sees the same sky. Latitude ≈ 29.5°N (Gulf Coast). Works with any pipeline: HDRP's
    /// physically based sky reacts to the directional light's rotation.
    /// </summary>
    public sealed class DayNightCycle : MonoBehaviour
    {
        public Light Sun;
        public Light Moon;
        public float Latitude = 29.5f;
        public float NorthHeading;
        public Gradient SunColor;
        public AnimationCurve SunIntensity = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        public float MaxSunIntensity = 1.2f;
        public float MoonIntensity = 0.08f;

        private void Reset()
        {
            SunColor = new Gradient();
            SunColor.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.45f, 0.25f), 0f), new GradientColorKey(new Color(1f, 0.85f, 0.7f), 0.25f), new GradientColorKey(new Color(1f, 0.97f, 0.92f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        }

        private void LateUpdate()
        {
            if (Sun == null || !ServiceRegistry.TryGet<GameSession>(out var session)) return;
            var now = session.World.Clock.Now;
            var dayOfYear = now.ToDateTime().DayOfYear;
            var declination = -23.44f * Mathf.Cos(2f * Mathf.PI / 365f * (dayOfYear + 10));
            var hourAngle = (now.HourFloat - 12f) * 15f;

            var lat = Latitude * Mathf.Deg2Rad;
            var dec = declination * Mathf.Deg2Rad;
            var ha = hourAngle * Mathf.Deg2Rad;
            var altitude = Mathf.Asin(Mathf.Sin(lat) * Mathf.Sin(dec) + Mathf.Cos(lat) * Mathf.Cos(dec) * Mathf.Cos(ha));
            var azimuth = Mathf.Atan2(-Mathf.Sin(ha), Mathf.Tan(dec) * Mathf.Cos(lat) - Mathf.Sin(lat) * Mathf.Cos(ha));

            var altDeg = altitude * Mathf.Rad2Deg;
            Sun.transform.rotation = Quaternion.Euler(altDeg, azimuth * Mathf.Rad2Deg + NorthHeading + 180f, 0f);
            var t = Mathf.Clamp01((altDeg + 4f) / 50f);
            Sun.intensity = MaxSunIntensity * SunIntensity.Evaluate(t);
            if (SunColor != null) Sun.color = SunColor.Evaluate(t);
            Sun.enabled = altDeg > -6f;

            if (Moon != null)
            {
                Moon.transform.rotation = Quaternion.Euler(-altDeg * 0.8f + 10f, azimuth * Mathf.Rad2Deg + 180f, 0f);
                Moon.intensity = altDeg < 0f ? MoonIntensity : 0f;
                Moon.enabled = altDeg < 2f;
            }
        }
    }
}
