using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace ELEC328.Assignment3
{
    public enum ParticleVisual
    {
        Sparks,
        Fountain,
        Crowd
    }

    public enum ParticleRenderStrategy
    {
        GameObjects,
        Instanced
    }

    /// <summary>
    /// A deliberately simple CPU particle simulation with two rendering paths.
    /// GameObjects creates one MeshRenderer and Transform per particle.
    /// Instanced submits the same mesh in batches through RenderMeshInstanced.
    /// </summary>
    public sealed class ParticleBenchmark : MonoBehaviour
    {
        private static readonly ProfilerMarker SimulationMarker = new("Assn3/Particle Simulation");
        private static readonly ProfilerMarker GameObjectMarker = new("Assn3/GameObject Transform Updates");
        private static readonly ProfilerMarker InstancedMarker = new("Assn3/Instanced Draw Submission");

        [Header("Experiment Variables")]
        [SerializeField] private ParticleVisual visual = ParticleVisual.Sparks;
        [SerializeField] private ParticleRenderStrategy renderStrategy = ParticleRenderStrategy.Instanced;
        [SerializeField, Min(1)] private int particleCount = 1000;
        [SerializeField] private bool animate = true;

        [Header("Shared Assets")]
        [SerializeField] private Mesh particleMesh;
        [SerializeField] private Material gameObjectMaterial;
        [SerializeField] private Material instancedMaterial;

        [Header("Simulation")]
        [SerializeField] private int randomSeed = 328;
        [SerializeField, Range(0.05f, 3f)] private float simulationSpeed = 1f;

        [Header("Sparks Burst")]
        [SerializeField, Min(0.1f)] private float sparkBurstInterval = 2.25f;
        [SerializeField] private bool automaticSparkBursts = true;

        private readonly List<GameObject> particleObjects = new();
        private Vector3[] positions;
        private Vector3[] velocities;
        private Vector3[] scales;
        private float[] ages;
        private float[] lifetimes;
        private float[] phases;
        private bool[] activeParticles;
        private Matrix4x4[] matrices;
        private RenderParams renderParams;
        private float sparkBurstTimer;
        private int sparkBurstIndex;
        private int builtCount = -1;
        private ParticleRenderStrategy builtStrategy = (ParticleRenderStrategy)(-1);
        // Nonuniform particle scales need both object/world matrices in each instance.
        // Stay below the default 511-instance limit for this shader configuration.
        private const int BatchSize = 500;

        public ParticleVisual Visual => visual;
        public int ParticleCount => particleCount;
        public ParticleRenderStrategy RenderStrategy => renderStrategy;
        public string DisplayName => visual.ToString();
        public float SimulationSpeed { get => simulationSpeed; set => simulationSpeed = Mathf.Clamp(value, 0.05f, 3f); }
        public bool Animate { get => animate; set => animate = value; }
        public int ApproximateDrawCalls => renderStrategy == ParticleRenderStrategy.GameObjects
            ? particleCount
            : Mathf.CeilToInt(particleCount / (float)BatchSize);

        [ContextMenu("Reset Simulation")]
        public void ResetSimulation()
        {
            EnsureBuilt();
            AllocateAndSeed();
        }

        public void SetParticleCount(int value)
        {
            particleCount = Mathf.Clamp(value, 1, 50000);
        }

        public void SetRenderStrategy(ParticleRenderStrategy value)
        {
            renderStrategy = value;
        }

        public void ToggleRenderStrategy()
        {
            renderStrategy = renderStrategy == ParticleRenderStrategy.GameObjects
                ? ParticleRenderStrategy.Instanced
                : ParticleRenderStrategy.GameObjects;
        }

        [ContextMenu("Pause or Resume")]
        public void TogglePause() => animate = !animate;

        public void SetAutomaticSparkBursts(bool enabled)
        {
            automaticSparkBursts = enabled;
        }

        public void TriggerSparkBurstAt(Vector3 worldOrigin)
        {
            if (visual != ParticleVisual.Sparks)
                return;

            transform.position = worldOrigin;
            EnsureBuilt();
            TriggerSparkBurst();
        }

        private void OnEnable()
        {
            EnsureBuilt();
        }

        private void OnDisable()
        {
            ClearGameObjects();
            builtStrategy = (ParticleRenderStrategy)(-1);
        }

        private void OnValidate()
        {
            particleCount = Mathf.Clamp(particleCount, 1, 50000);
            simulationSpeed = Mathf.Max(0.01f, simulationSpeed);
            sparkBurstInterval = Mathf.Max(0.1f, sparkBurstInterval);
        }

        private void Update()
        {
            EnsureBuilt();
            if (positions == null || positions.Length == 0)
                return;

            float dt = animate ? Mathf.Min(Time.deltaTime, 0.05f) * simulationSpeed : 0f;
            using (SimulationMarker.Auto())
            {
                Simulate(dt);
                BuildMatrices();
            }

            if (renderStrategy == ParticleRenderStrategy.GameObjects)
            {
                using (GameObjectMarker.Auto())
                    UpdateGameObjects();
            }
            else
            {
                using (InstancedMarker.Auto())
                    SubmitInstancedDraws();
            }
        }

        private void EnsureBuilt()
        {
            if (builtCount == particleCount && builtStrategy == renderStrategy && positions != null)
                return;

            ClearGameObjects();
            if (builtCount != particleCount || positions == null)
                AllocateAndSeed();
            if (renderStrategy == ParticleRenderStrategy.GameObjects)
                BuildGameObjects();

            if (instancedMaterial != null)
            {
                renderParams = new RenderParams(instancedMaterial)
                {
                    worldBounds = new Bounds(transform.position, Vector3.one * 200f),
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = false,
                    layer = gameObject.layer
                };
            }

            builtCount = particleCount;
            builtStrategy = renderStrategy;
        }

        private void AllocateAndSeed()
        {
            positions = new Vector3[particleCount];
            velocities = new Vector3[particleCount];
            scales = new Vector3[particleCount];
            ages = new float[particleCount];
            lifetimes = new float[particleCount];
            phases = new float[particleCount];
            activeParticles = new bool[particleCount];
            matrices = new Matrix4x4[particleCount];

            Random.State oldState = Random.state;
            Random.InitState(randomSeed + (int)visual * 1009);
            for (int i = 0; i < particleCount; i++)
            {
                SeedParticle(i, visual != ParticleVisual.Sparks);
                if (visual == ParticleVisual.Sparks && !automaticSparkBursts)
                    activeParticles[i] = false;
            }
            Random.state = oldState;
            sparkBurstTimer = sparkBurstInterval;
            sparkBurstIndex = 0;
        }

        private void SeedParticle(int index, bool stagger)
        {
            activeParticles[index] = true;
            phases[index] = Random.value * Mathf.PI * 2f;
            switch (visual)
            {
                case ParticleVisual.Sparks:
                    lifetimes[index] = Random.Range(0.45f, 1.6f);
                    ages[index] = stagger ? Random.value * lifetimes[index] : 0f;
                    positions[index] = new Vector3(Random.Range(-0.3f, 0.3f), 0.25f, Random.Range(-0.3f, 0.3f));
                    Vector2 sparkCircle = Random.insideUnitCircle.normalized * Random.Range(1.5f, 5.5f);
                    velocities[index] = new Vector3(sparkCircle.x, Random.Range(5f, 11f), sparkCircle.y);
                    float sparkSize = Random.Range(0.035f, 0.09f);
                    scales[index] = new Vector3(sparkSize, sparkSize * Random.Range(3f, 8f), sparkSize);
                    break;

                case ParticleVisual.Fountain:
                    lifetimes[index] = Random.Range(1.4f, 3.1f);
                    ages[index] = stagger ? Random.value * lifetimes[index] : 0f;
                    positions[index] = new Vector3(Random.Range(-0.25f, 0.25f), 0.2f, Random.Range(-0.25f, 0.25f));
                    Vector2 waterCircle = Random.insideUnitCircle * Random.Range(0.4f, 2.3f);
                    velocities[index] = new Vector3(waterCircle.x, Random.Range(7f, 12f), waterCircle.y);
                    float dropSize = Random.Range(0.055f, 0.13f);
                    scales[index] = new Vector3(dropSize, dropSize * 1.6f, dropSize);
                    break;

                default:
                    int columns = Mathf.CeilToInt(Mathf.Sqrt(particleCount));
                    int row = index / columns;
                    int column = index % columns;
                    // Fixed footprint keeps every population outside the Roost rails.
                    int rows = Mathf.CeilToInt(particleCount / (float)columns);
                    positions[index] = new Vector3(
                        columns > 1 ? (column / (float)(columns - 1) - 0.5f) * 8f : 0f,
                        0.34f,
                        rows > 1 ? (row / (float)(rows - 1) - 0.5f) * 4f : 0f);
                    velocities[index] = Vector3.zero;
                    lifetimes[index] = float.PositiveInfinity;
                    ages[index] = 0f;
                    float personScale = Random.Range(0.75f, 1.25f);
                    scales[index] = new Vector3(0.15f, 0.34f * personScale, 0.15f);
                    break;
            }
        }

        private void Simulate(float dt)
        {
            if (visual == ParticleVisual.Crowd)
            {
                if (dt <= 0f)
                    return;
                for (int i = 0; i < particleCount; i++)
                {
                    ages[i] += dt;
                    Vector3 p = positions[i];
                    p.y = 0.34f + Mathf.Sin(ages[i] * 2.2f + phases[i]) * 0.035f;
                    positions[i] = p;
                }
                return;
            }

            if (visual == ParticleVisual.Sparks)
            {
                if (automaticSparkBursts)
                {
                    sparkBurstTimer -= dt;
                    if (sparkBurstTimer <= 0f)
                        TriggerSparkBurst();
                }

                for (int i = 0; i < particleCount; i++)
                {
                    if (!activeParticles[i])
                        continue;
                    ages[i] += dt;
                    velocities[i].y -= 14f * dt;
                    positions[i] += velocities[i] * dt;
                    if (ages[i] >= lifetimes[i] || positions[i].y < 0f)
                        activeParticles[i] = false;
                }
                return;
            }

            for (int i = 0; i < particleCount; i++)
            {
                ages[i] += dt;
                velocities[i].y -= 9.81f * dt;
                positions[i] += velocities[i] * dt;
                if (ages[i] >= lifetimes[i] || positions[i].y < 0f)
                    SeedParticle(i, false);
            }
        }

        [ContextMenu("Trigger Spark Burst")]
        public void TriggerSparkBurst()
        {
            if (visual != ParticleVisual.Sparks) return;
            EnsureBuilt();
            Random.State oldState = Random.state;
            sparkBurstIndex++;
            Random.InitState(randomSeed + sparkBurstIndex * 104729);
            for (int i = 0; i < particleCount; i++)
                SeedParticle(i, false);
            Random.state = oldState;
            sparkBurstTimer = sparkBurstInterval;
        }

        private void BuildMatrices()
        {
            Matrix4x4 root = transform.localToWorldMatrix;
            for (int i = 0; i < particleCount; i++)
            {
                if (!activeParticles[i])
                {
                    matrices[i] = root * Matrix4x4.TRS(positions[i], Quaternion.identity, Vector3.zero);
                    continue;
                }

                Quaternion rotation = Quaternion.identity;
                if (visual != ParticleVisual.Crowd && velocities[i].sqrMagnitude > 0.001f)
                    rotation = Quaternion.FromToRotation(Vector3.up, velocities[i].normalized);
                else if (visual == ParticleVisual.Crowd)
                    rotation = Quaternion.Euler(0f, phases[i] * Mathf.Rad2Deg, 0f);

                matrices[i] = root * Matrix4x4.TRS(positions[i], rotation, scales[i]);
            }
        }

        private void BuildGameObjects()
        {
            if (particleMesh == null || gameObjectMaterial == null)
                return;

            for (int i = 0; i < particleCount; i++)
            {
                GameObject particle = new($"Particle {i:00000}");
                particle.transform.SetParent(transform, false);
                MeshFilter filter = particle.AddComponent<MeshFilter>();
                filter.sharedMesh = particleMesh;
                MeshRenderer renderer = particle.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = gameObjectMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                particleObjects.Add(particle);
            }
        }

        private void UpdateGameObjects()
        {
            int count = Mathf.Min(particleCount, particleObjects.Count);
            for (int i = 0; i < count; i++)
            {
                GameObject particle = particleObjects[i];
                if (particle.activeSelf != activeParticles[i])
                    particle.SetActive(activeParticles[i]);
                if (!activeParticles[i])
                    continue;

                Matrix4x4 localMatrix = Matrix4x4.TRS(positions[i],
                    visual == ParticleVisual.Crowd
                        ? Quaternion.Euler(0f, phases[i] * Mathf.Rad2Deg, 0f)
                        : Quaternion.FromToRotation(Vector3.up, velocities[i].normalized),
                    scales[i]);
                Transform t = particle.transform;
                t.localPosition = localMatrix.GetPosition();
                t.localRotation = localMatrix.rotation;
                t.localScale = localMatrix.lossyScale;
            }
        }

        private void SubmitInstancedDraws()
        {
            if (particleMesh == null || instancedMaterial == null || matrices == null)
                return;

            renderParams.worldBounds = new Bounds(transform.position, Vector3.one * 200f);
            for (int start = 0; start < particleCount; start += BatchSize)
            {
                int count = Mathf.Min(BatchSize, particleCount - start);
                Graphics.RenderMeshInstanced(renderParams, particleMesh, 0, matrices, count, start);
            }
        }

        private void ClearGameObjects()
        {
            foreach (GameObject particle in particleObjects)
            {
                if (particle == null)
                    continue;
                if (Application.isPlaying)
                    Destroy(particle);
                else
                    DestroyImmediate(particle);
            }
            particleObjects.Clear();
        }

        private void OnDestroy()
        {
            ClearGameObjects();
        }

        // Optional API for configuring another instance with the supplied assets.
        public void Configure(ParticleVisual newVisual, int newCount, Mesh mesh, Material goMaterial, Material gpuMaterial)
        {
            visual = newVisual;
            particleCount = newCount;
            particleMesh = mesh;
            gameObjectMaterial = goMaterial;
            instancedMaterial = gpuMaterial;
            renderStrategy = ParticleRenderStrategy.Instanced;
        }
    }
}
