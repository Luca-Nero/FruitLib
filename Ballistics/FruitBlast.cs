using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// Blast-wave physics for <see cref="ExplosionSpec.ChargeKgTNT"/>: how much pressure reaches
    /// a point, from Hopkinson-Cranz scaling. Everything follows the scaled distance
    /// Z = R / W^(1/3) (m/kg^⅓), so a charge eight times heavier reaches the same pressure twice
    /// as far.
    ///
    /// Peak incident overpressure is the Mills (1987) fit to the Kingery-Bulmash free-air
    /// curves: within roughly 20% from Z ~0.5 out, where injury is decided (Z 2 ≈ 250 kPa,
    /// Z 5 ≈ 30 kPa); nearer in it runs high, which only matters below the cap anyway. A
    /// surface in the wave's way - a body - sees the reflected pressure, which is at least twice
    /// that and up to eight times it for strong shocks (Rankine-Hugoniot, ideal air).
    /// </summary>
    public static class FruitBlast
    {
        /// <summary>Sea-level ambient pressure, kPa.</summary>
        public const float AmbientKPa = 101.325f;
        /// <summary>Near-field cap, kPa: the fit diverges inside the fireball, and past tens of
        /// MPa tissue is gone however high the number goes.</summary>
        public const float MaxKPa = 50000f;

        /// <summary>Scaled distance, m/kg^⅓.</summary>
        public static float Scaled(float metres, float kgTnt)
            => Mathf.Max(0.01f, metres) / Mathf.Pow(Mathf.Max(1e-4f, kgTnt), 1f / 3f);

        /// <summary>Peak incident (side-on) overpressure at scaled distance <paramref name="z"/>, kPa.</summary>
        public static float IncidentKPa(float z)
        {
            z = Mathf.Max(0.05f, z);
            float p = 1772f / (z * z * z) - 114f / (z * z) + 108f / z;
            return Mathf.Clamp(p, 0f, MaxKPa);
        }

        /// <summary>Peak reflected overpressure off a surface facing the wave, kPa.</summary>
        public static float ReflectedKPa(float incidentKPa)
        {
            float p = Mathf.Max(0f, incidentKPa), p0 = AmbientKPa;
            return Mathf.Min(MaxKPa, 2f * p * (7f * p0 + 4f * p) / (7f * p0 + p));
        }

        /// <summary>Peak incident overpressure <paramref name="metres"/> from <paramref name="kgTnt"/>, kPa.</summary>
        public static float IncidentKPa(float metres, float kgTnt) => IncidentKPa(Scaled(metres, kgTnt));

        /// <summary>How far out the incident overpressure still reaches <paramref name="kPa"/>, metres.</summary>
        public static float RangeFor(float kgTnt, float kPa)
        {
            // The fit falls monotonically over any range we care about: bisect on Z.
            float lo = 0.05f, hi = 200f;
            for (int i = 0; i < 40; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (IncidentKPa(mid) > kPa) lo = mid; else hi = mid;
            }
            return hi * Mathf.Pow(Mathf.Max(1e-4f, kgTnt), 1f / 3f);
        }
    }
}
