using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

public class Simulation3D : MonoBehaviour
{
    public event System.Action SimulationStepCompleted;

    [Header("Settings")]
    public float timeScale = 1;
    public bool fixedTimeStep;
    public int iterationsPerFrame;
    public float gravity = -10;

    [Range(0, 1)]
    public float collisionDamping = 0.05f;

    public float smoothingRadius = 0.2f;
    public float targetDensity;
    public float pressureMultiplier;
    public float nearPressureMultiplier;
    public float viscosityStrength;

    [Header("Wind")]
    // Wind Power
    public float3 windDirection = new(0, 0, 0);
    public float windStrength = 0f;
    // Wind Area Bounds
    public float3 windAreaMin = new(0, 0, 0);
    public float3 windAreaMax = new(0, 0, 0);

    [Header("References")]
    public ComputeShader compute;
    public Spawner3D spawner;
    public ParticleDisplay3D display;
    public Transform floorDisplay;
    // OBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB
    public TestedObject testedObject;

    // Buffers
    public ComputeBuffer PositionBuffer { get; private set; }
    public ComputeBuffer VelocityBuffer { get; private set; }
    public ComputeBuffer DensityBuffer { get; private set; }
    public ComputeBuffer predictedPositionsBuffer;
    ComputeBuffer spatialIndices;
    ComputeBuffer spatialOffsets;

    // Tested Object Buffers
    public ComputeBuffer PointsBuffer { get; private set; }
    public ComputeBuffer TrianglesBuffer { get; private set; }
    ComputeBuffer pointsIndices;
    ComputeBuffer pointsOffsets;
    //Dictionary<int, float3[]> pointTriangles = new();
    //ComputeBuffer MapBuffer;

    // Kernel IDs
    const int externalForcesKernel = 0;
    const int spatialHashKernel = 1;
    const int densityKernel = 2;
    const int pressureKernel = 3;
    const int viscosityKernel = 4;
    const int updatePositionsKernel = 5;

    // Tested Object kernel IDs
    //const int updatePointsHashKernel = 6;

    GPUSort gpuSort;

    //// Tested Object GPUSort
    //GPUSort testedObjectGPUSort;

    // State
    bool isPaused;
    bool pauseNextFrame;
    Spawner3D.SpawnData spawnData;

    //private void Awake()
    //{
    //    testedObject = GetComponent<TestedObject>();
    //}
    Dictionary<float3, float3[]> pointsTrianglesMap;

    void Start()
    {
        testedObject.InitializeMesh();

        Debug.Log("Controls: Space = Play/Pause, R = Reset");
        Debug.Log("Use transform tool in scene to scale/rotate simulation bounding box.");

        float deltaTime = 1 / 60f;
        Time.fixedDeltaTime = deltaTime;

        spawnData = spawner.GetSpawnData();

        // Create buffers
        int numParticles = spawnData.points.Length;
        PositionBuffer = ComputeHelper.CreateStructuredBuffer<float3>(numParticles);
        predictedPositionsBuffer = ComputeHelper.CreateStructuredBuffer<float3>(numParticles);
        VelocityBuffer = ComputeHelper.CreateStructuredBuffer<float3>(numParticles);
        DensityBuffer = ComputeHelper.CreateStructuredBuffer<float2>(numParticles);
        spatialIndices = ComputeHelper.CreateStructuredBuffer<uint3>(numParticles);
        spatialOffsets = ComputeHelper.CreateStructuredBuffer<uint>(numParticles);

        // Create Tested Object buffers
        int numPoints = testedObject.vertices.Length;
        int numTriangles = testedObject.triangles.Length;
        //int numPoints = 515;
        //int numTriangles = 2304;
        PointsBuffer = ComputeHelper.CreateStructuredBuffer<float3>(numPoints);
        TrianglesBuffer = ComputeHelper.CreateStructuredBuffer<uint>(numTriangles);
        pointsIndices = ComputeHelper.CreateStructuredBuffer<uint3>(numPoints);
        pointsOffsets = ComputeHelper.CreateStructuredBuffer<uint>(numPoints);
        //MapBuffer = ComputeHelper.CreateStructuredBuffer<MyDictionary>(numPoints);

        // Set buffer data
        SetInitialBufferData(spawnData);

        // Set Tested Object buffer data
        SetPointsTriangles(testedObject);

        // Init compute
        ComputeHelper.SetBuffer(compute, PositionBuffer, "Positions", externalForcesKernel, updatePositionsKernel);
        ComputeHelper.SetBuffer(compute, predictedPositionsBuffer, "PredictedPositions", externalForcesKernel, spatialHashKernel, densityKernel, pressureKernel, viscosityKernel, updatePositionsKernel);
        ComputeHelper.SetBuffer(compute, spatialIndices, "SpatialIndices", spatialHashKernel, densityKernel, pressureKernel, viscosityKernel);
        ComputeHelper.SetBuffer(compute, spatialOffsets, "SpatialOffsets", spatialHashKernel, densityKernel, pressureKernel, viscosityKernel);
        ComputeHelper.SetBuffer(compute, DensityBuffer, "Densities", densityKernel, pressureKernel, viscosityKernel);
        ComputeHelper.SetBuffer(compute, VelocityBuffer, "Velocities", externalForcesKernel, pressureKernel, viscosityKernel, updatePositionsKernel);

        // Init Tested Object
        ComputeHelper.SetBuffer(compute, PointsBuffer, "Points", spatialHashKernel); // , updatePointsHashKernel
        ComputeHelper.SetBuffer(compute, TrianglesBuffer, "Triangles");
        ComputeHelper.SetBuffer(compute, pointsIndices, "PointsIndices", spatialHashKernel);
        ComputeHelper.SetBuffer(compute, pointsOffsets, "PointsOffsets", spatialHashKernel);

        //ComputeHelper.SetBuffer(compute, MapBuffer, "Map");

        compute.SetInt("numParticles", PositionBuffer.count);

        // Tested Object
        compute.SetInt("numPoints", PointsBuffer.count);

        gpuSort = new();
        gpuSort.SetBuffers(spatialIndices, spatialOffsets);

        // Tessted Object 
        gpuSort.SetPointsBuffers(pointsIndices, pointsOffsets);

        // Init display
        display.Init(this);
    }

    void FixedUpdate()
    {
        // Run simulation if in fixed timestep mode
        if (fixedTimeStep)
        {
            RunSimulationFrame(Time.fixedDeltaTime);
        }
    }

    void Update()
    {
        // Run simulation if not in fixed timestep mode
        // (skip running for first few frames as timestep can be a lot higher than usual)
        if (!fixedTimeStep && Time.frameCount > 10)
        {
            RunSimulationFrame(Time.deltaTime);
        }

        if (pauseNextFrame)
        {
            isPaused = true;
            pauseNextFrame = false;
        }
        floorDisplay.transform.localScale = new Vector3(1, 1 / transform.localScale.y * 0.1f, 1);

        HandleInput();
    }

    void RunSimulationFrame(float frameTime)
    {
        //ComputeHelper.Dispatch(compute, PointsBuffer.count, kernelIndex: updatePointsHashKernel); // , kernelIndex: updatePointsHashKernel

        if (!isPaused)
        {
            float timeStep = frameTime / iterationsPerFrame * timeScale;

            UpdateSettings(timeStep);

            for (int i = 0; i < iterationsPerFrame; i++)
            {
                RunSimulationStep();
                SimulationStepCompleted?.Invoke();
            }
        }
    }

    void RunSimulationStep()
    {

        ComputeHelper.Dispatch(compute, PositionBuffer.count, kernelIndex: externalForcesKernel);
        ComputeHelper.Dispatch(compute, PositionBuffer.count, kernelIndex: spatialHashKernel);
        gpuSort.SortAndCalculateOffsets();
        ComputeHelper.Dispatch(compute, PositionBuffer.count, kernelIndex: densityKernel);
        ComputeHelper.Dispatch(compute, PositionBuffer.count, kernelIndex: pressureKernel);
        ComputeHelper.Dispatch(compute, PositionBuffer.count, kernelIndex: viscosityKernel);
        ComputeHelper.Dispatch(compute, PositionBuffer.count, kernelIndex: updatePositionsKernel);
    }

    void UpdateSettings(float deltaTime)
    {
        Vector3 simBoundsSize = transform.localScale;
        Vector3 simBoundsCentre = transform.position;

        // Wind
        Vector3 windDirectionVector = windDirection;
        Vector3 windAreaMinVector = windAreaMin;
        Vector3 windAreaMaxVector = windAreaMax;

        compute.SetFloat("deltaTime", deltaTime);
        compute.SetFloat("gravity", gravity);

        // Add wind power to Compute Shader 
        compute.SetVector("windDirection", windDirectionVector);
        compute.SetFloat("windStrength", windStrength);

        // Add wind Range to Compute Shader 
        compute.SetVector("windAreaMin", windAreaMinVector);
        compute.SetVector("windAreaMax", windAreaMaxVector);

        compute.SetFloat("collisionDamping", collisionDamping);
        compute.SetFloat("smoothingRadius", smoothingRadius);
        compute.SetFloat("targetDensity", targetDensity);
        compute.SetFloat("pressureMultiplier", pressureMultiplier);
        compute.SetFloat("nearPressureMultiplier", nearPressureMultiplier);
        compute.SetFloat("viscosityStrength", viscosityStrength);
        compute.SetVector("boundsSize", simBoundsSize);
        compute.SetVector("centre", simBoundsCentre);

        compute.SetMatrix("localToWorld", transform.localToWorldMatrix);
        compute.SetMatrix("worldToLocal", transform.worldToLocalMatrix);
    }

    void SetInitialBufferData(Spawner3D.SpawnData spawnData)
    {
        float3[] allPoints = new float3[spawnData.points.Length];
        System.Array.Copy(spawnData.points, allPoints, spawnData.points.Length);

        PositionBuffer.SetData(allPoints);
        predictedPositionsBuffer.SetData(allPoints);
        VelocityBuffer.SetData(spawnData.velocities);
    }

    void HandleInput()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            isPaused = !isPaused;
        }

        if (Input.GetKeyDown(KeyCode.RightArrow))
        {
            isPaused = false;
            pauseNextFrame = true;
        }

        if (Input.GetKeyDown(KeyCode.R))
        {
            isPaused = true;
            SetInitialBufferData(spawnData);
        }
    }

    void OnDestroy()
    {
        ComputeHelper.Release(PositionBuffer, predictedPositionsBuffer, VelocityBuffer, DensityBuffer, spatialIndices, spatialOffsets, PointsBuffer, TrianglesBuffer, pointsIndices, pointsOffsets); // MapBuffer
    }

    void OnDrawGizmos()
    {
        // Draw Bounds
        var m = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(0, 1, 0, 0.5f);
        Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
        Gizmos.matrix = m;

    }

    void SetPointsTriangles(TestedObject testedObject)
    {
        float3[] allPoints = new float3[testedObject.vertices.Length];
        //System.Array.Copy(testedObject.vertices, allPoints, testedObject.vertices.Length);
        for (int i = 0; i < testedObject.vertices.Length; i++)
        {
            allPoints[i].x = testedObject.vertices[i].x;
            allPoints[i].y = testedObject.vertices[i].y;
            allPoints[i].z = testedObject.vertices[i].z;
        }


        int[] allTriagnles = new int[testedObject.triangles.Length];
        System.Array.Copy(testedObject.triangles, allTriagnles, testedObject.triangles.Length);

        PointsBuffer.SetData(allPoints);
        TrianglesBuffer.SetData(allTriagnles);

        //MapBuffer.SetData(myDictionary);
    }

    // Set Tested Object buffers 
    void MakeDictionary(TestedObject testedObject)
    {
        float3[] allPoints = new float3[testedObject.vertices.Length];
        //System.Array.Copy(testedObject.vertices, allPoints, testedObject.vertices.Length);
        for (int i = 0; i < testedObject.vertices.Length; i++)
        {
            allPoints[i].x = testedObject.vertices[i].x;
            allPoints[i].y = testedObject.vertices[i].y;
            allPoints[i].z = testedObject.vertices[i].z;
        }


        int[] allTriagnles = new int[testedObject.triangles.Length];
        System.Array.Copy(testedObject.triangles, allTriagnles, testedObject.triangles.Length);

        pointsTrianglesMap = new();

        for (int i = 0; i < allPoints.Length; i++)
        {
            int counter = 0;
            float3[] myTriangles = new float3[allTriagnles.Length];
            for (int j = 0; j < allTriagnles.Length; j++)
            {
                if (i == allTriagnles[j])
                {
                    if (j % 3 == 0)
                    {
                        myTriangles[counter++] = allPoints[allTriagnles[j]];
                        myTriangles[counter++] = allPoints[allTriagnles[j + 1]];
                        myTriangles[counter++] = allPoints[allTriagnles[j + 2]];
                    }
                    if (j % 3 == 1)
                    {
                        myTriangles[counter++] = allPoints[allTriagnles[j - 1]];
                        myTriangles[counter++] = allPoints[allTriagnles[j]];
                        myTriangles[counter++] = allPoints[allTriagnles[j + 1]];
                    }
                    if (j % 3 == 2)
                    {
                        myTriangles[counter++] = allPoints[allTriagnles[j - 2]];
                        myTriangles[counter++] = allPoints[allTriagnles[j - 1]];
                        myTriangles[counter++] = allPoints[allTriagnles[j]];
                    }
                }
            }

            pointsTrianglesMap.Add(allPoints[i], myTriangles);
        }

    }


    public void GetAndSetShaderData()
    {
        float3[] PredictedPositions = new float3[predictedPositionsBuffer.count];
        predictedPositionsBuffer.GetData(PredictedPositions);
        //AsyncGPUReadback.Request(predictedPositionsBuffer, (asyncGPUReadbackRequest) => 
        //{
        //    if(asyncGPUReadbackRequest.done)
        //        predictedPositionsBuffer.GetData(PredictedPositions);
        //});

        float3[] Velocities = new float3[VelocityBuffer.count];
        VelocityBuffer.GetData(Velocities);

        uint3[] SpatialIndices = new uint3[spatialIndices.count];
        spatialIndices.GetData(SpatialIndices);

        uint[] SpatialOffsets = new uint[spatialOffsets.count];
        spatialOffsets.GetData(SpatialOffsets);

        float3[] Points = new float3[PointsBuffer.count];
        PointsBuffer.GetData(Points);

        uint[] Triangles = new uint[TrianglesBuffer.count];
        TrianglesBuffer.GetData(Triangles);

        uint3[] PointsIndices = new uint3[pointsIndices.count];
        pointsIndices.GetData(PointsIndices);

        uint[] PointsOffsets = new uint[pointsOffsets.count];
        pointsOffsets.GetData(PointsOffsets);




        VelocityBuffer.SetData(Velocities);
    }


    void CheckCollision(float3 partilce)
    {
        int3 originCell = GetCell3D(partilce, smoothingRadius);

        //Neighbour search
        for (int i = 0; i < 27; i++)
        {
            uint hash = HashCell3D(originCell + offsets3D[i]);
            uint key = KeyFromHash(hash, PositionBuffer.count);
            uint currIndex = PointsOffsets[key];

            while (currIndex < PositionBuffer.count)
            {
                uint3 indexData = PointsIndices[currIndex];
                currIndex++;
                // Exit if no longer looking at correct bin
                if (indexData[2] != key)
                    break;
                // Skip if hash does not match
                if (indexData[1] != hash)
                    continue;

                uint neighbourIndex = indexData[0];

                float3 neighbourPoint = Points[neighbourIndex];

                for (int j = 0; j < pointsTrianglesMap[neighbourPoint].Length; j++)
                {
                    if (pointsTrianglesMap[neighbourPoint][j].x == 0 &&
                        pointsTrianglesMap[neighbourPoint][j].y == 0 &&
                        pointsTrianglesMap[neighbourPoint][j].z == 0)
                        break;

                    var isCollide = CollisionDetection.IsCollided
                    (
                        pointsTrianglesMap[neighbourPoint][j],
                        pointsTrianglesMap[neighbourPoint][j + 1],
                        pointsTrianglesMap[neighbourPoint][j + 2],
                        partilce,
                        smoothingRadius
                    );


                }
                
            }
        }
    }
}
