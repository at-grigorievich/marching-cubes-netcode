using Unity.Mathematics;

namespace MineGenerator
{
	public static class Noise
	{
		/// <summary>
		/// Значение шума в диапазоне [0..1].
		///
		/// Раньше здесь складывались шесть вызовов <c>Mathf.PerlinNoise</c>. Это не 3D-шум
		/// (по диагоналям видны повторы), шесть выборок вместо одной — и главное,
		/// <c>Mathf.PerlinNoise</c> уходит в нативный вызов движка, который Burst
		/// скомпилировать не может: джоб с ним молча откатывался на Mono.
		/// </summary>
		public static float PerlinNoise3D(float x, float y, float z) =>
			math.saturate(noise.snoise(new float3(x, y, z)) * 0.5f + 0.5f);
	}
}
