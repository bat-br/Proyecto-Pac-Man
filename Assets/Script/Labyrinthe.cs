using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.AI;
using System;
using System.Threading;

public class Labyrinthe : MonoBehaviour
{
    [Header("Objetos en la escena")]
    public GameObject player;
    public Transform entrance;
    public Transform exit;
    public GameObject winCanvas;
    public Transform clydeCorner; // Esquina de huida para Clyde (el fantasma naranja)

    [Header("Variables de configuracion")]
    public float detectionRange = 1f;
    public float exitRange = 1f;
    public float minDistanceFromEntrance = 3f;
    public int aiUpdateIntervalMs = 150; // Cada cuantos ms piensa el hilo secundario

    [Header("Separacion entre fantasmas")]
    public float separationRadius = 2.0f;   // Distancia a la que empiezan a repelerse
    public float separationDistance = 3.0f; // Que tan lejos se apartan al chocar

    // Agentes de IA
    List<NavMeshAgent> agents = new();

    NavMeshTriangulation triangulation;
    Vector3 entrancePos;

    // Variables para el algoritmo
    float detectionRangeSqr;
    float exitRangeSqr;
    float minDistanceSqr;
    bool gameWon = false;

    // --- CONCURRENCIA ---
    private Queue<Action> mainThreadActions = new Queue<Action>();
    private Thread aiThread;
    private volatile bool isRunning = false;

    // Variables compartidas (Hilo Principal -> Hilo Secundario)
    private readonly object stateLock = new object();
    private Vector3 sharedPlayerPos;
    private Vector3 sharedPlayerForward;
    private Vector3[] sharedAgentPositions;
    private Vector3 sharedClydeCorner;

    System.Random random = new();

    void Start()
    {
        entrancePos = entrance.position;
        triangulation = NavMesh.CalculateTriangulation();

        detectionRangeSqr = detectionRange * detectionRange;
        exitRangeSqr = exitRange * exitRange;
        minDistanceSqr = minDistanceFromEntrance * minDistanceFromEntrance;

        winCanvas.SetActive(false);

        FindAllEnemies();

        // Inicializar estado compartido antes de arrancar el hilo
        sharedAgentPositions = new Vector3[agents.Count];
        sharedClydeCorner = clydeCorner != null ? clydeCorner.position : entrancePos;
        UpdateSharedState();

        // Iniciar el hilo secundario de IA
        isRunning = true;
        aiThread = new Thread(GhostAILoop);
        aiThread.IsBackground = true; // Asegura que el hilo muera si se cierra el juego
        aiThread.Start();
    }

    void Update()
    {
        if (gameWon) return;

        Vector3 playerPos = player.transform.position;
        bool playerCaught = false;

        foreach (var agent in agents)
        {
            if (!agent.enabled) continue;
            if ((agent.transform.position - playerPos).sqrMagnitude < detectionRangeSqr)
            {
                playerCaught = true;
                break;
            }
        }

        // Si capturan al jugador
        if (playerCaught)
        {
            TeleportPlayerToEntrance();
            RelocateAllNPC();
            return;
        }

        // Si el jugador llega al area de salida
        if ((playerPos - exit.position).sqrMagnitude < exitRangeSqr)
        {
            WinGame();
            return;
        }

        // 1. Actualizar los datos seguros para que el hilo secundario los lea
        UpdateSharedState();

        // 2. Ejecutar las decisiones calculadas por el hilo secundario en el hilo principal
        lock (mainThreadActions)
        {
            while (mainThreadActions.Count > 0)
            {
                mainThreadActions.Dequeue().Invoke();
            }
        }
    }

    // Copia datos de Unity a estructuras primitivas thread-safe
    private void UpdateSharedState()
    {
        lock (stateLock)
        {
            sharedPlayerPos = player.transform.position;
            sharedPlayerForward = player.transform.forward;

            for (int i = 0; i < agents.Count; i++)
            {
                if (agents[i] != null)
                {
                    sharedAgentPositions[i] = agents[i].transform.position;
                }
            }
        }
    }

    // --- HILO SECUNDARIO: CEREBRO DE LOS FANTASMAS ---
    private void GhostAILoop()
    {
        while (isRunning)
        {
            Thread.Sleep(aiUpdateIntervalMs);

            if (gameWon) continue;

            // Leer una instantanea segura del estado actual
            Vector3 pPos;
            Vector3 pFwd;
            Vector3[] aPos;
            Vector3 corner;

            lock (stateLock)
            {
                if (sharedAgentPositions == null || sharedAgentPositions.Length == 0) continue;

                pPos = sharedPlayerPos;
                pFwd = sharedPlayerForward;
                aPos = (Vector3[])sharedAgentPositions.Clone();
                corner = sharedClydeCorner;
            }

            // Calcular destino individual segun el indice del fantasma
            for (int i = 0; i < aPos.Length; i++)
            {
                int ghostIndex = i; // Copia local para evitar problemas de closure en la lambda
                Vector3 targetDestination = CalculateGhostTarget(ghostIndex, pPos, pFwd, aPos, corner);

                // Encolar la orden para el NavMeshAgent en el hilo principal
                lock (mainThreadActions)
                {
                    mainThreadActions.Enqueue(() =>
                    {
                        if (ghostIndex < agents.Count && agents[ghostIndex] != null &&
                            agents[ghostIndex].enabled && !agents[ghostIndex].isStopped)
                        {
                            if (NavMesh.SamplePosition(targetDestination, out NavMeshHit hit, 2.0f, NavMesh.AllAreas))
                            {
                                agents[ghostIndex].SetDestination(hit.position);
                            }
                            else
                            {
                                agents[ghostIndex].SetDestination(targetDestination);
                            }
                        }
                    });
                }
            }
        }
    }

    // Logica pura de matematicas (100% segura en hilo secundario)
    private Vector3 CalculateGhostTarget(int index, Vector3 playerPos, Vector3 playerFwd, Vector3[] agentPositions, Vector3 corner)
    {
        // 1. Verificar si hay otros fantasmas demasiado cerca
        Vector3 repulsion = CalculateSeparation(index, agentPositions);
        if (repulsion != Vector3.zero)
        {
            // Alejarse temporalmente en direccion contraria al vecino
            return agentPositions[index] + (repulsion.normalized * separationDistance);
        }

        // 2. Si tienen espacio libre, aplicar el comportamiento clasico de Pac-Man
        switch (index % 4)
        {
            case 0: // BLINKY (Rojo): Persecucion directa
                return playerPos;

            case 1: // PINKY (Rosa): Emboscada 4 unidades frente al jugador
                return playerPos + (playerFwd * 4f);

            case 2: // INKY (Cian): Flanqueo usando la posicion de Blinky (indice 0)
                Vector3 pivot = playerPos + (playerFwd * 2f);
                Vector3 blinkyPos = agentPositions.Length > 0 ? agentPositions[0] : playerPos;
                return pivot + (pivot - blinkyPos);

            case 3: // CLYDE (Naranja): Persigue si esta lejos (mas de 6m), huye a su esquina si esta cerca
                float distSqr = (agentPositions[index] - playerPos).sqrMagnitude;
                return (distSqr > 36f) ? playerPos : corner;

            default:
                return playerPos;
        }
    }

    // Calcula el vector de repulsion respecto a otros fantasmas cercanos
    private Vector3 CalculateSeparation(int index, Vector3[] agentPositions)
    {
        Vector3 myPos = agentPositions[index];
        Vector3 repulsion = Vector3.zero;
        float radiusSqr = separationRadius * separationRadius;

        for (int j = 0; j < agentPositions.Length; j++)
        {
            if (j == index) continue;

            Vector3 diff = myPos - agentPositions[j];
            diff.y = 0f; // Mantener el calculo en el plano horizontal

            float sqrDist = diff.sqrMagnitude;
            if (sqrDist < radiusSqr)
            {
                if (sqrDist < 0.0001f)
                {
                    // Si estan exactamente encima uno del otro, empujar en direcciones distintas
                    float angle = index * 1.57f; // 90 grados por indice
                    repulsion += new Vector3((float)Math.Cos(angle), 0f, (float)Math.Sin(angle));
                }
                else
                {
                    // Entre mas cerca esten, mas fuerte es el empuje
                    repulsion += diff.normalized / (float)Math.Sqrt(sqrDist);
                }
            }
        }

        return repulsion;
    }

    // --- DETENCION LIMPIA DEL HILO ---
    void OnDestroy()
    {
        isRunning = false;
        if (aiThread != null && aiThread.IsAlive)
        {
            aiThread.Join(100);
        }
    }

    // --- METODOS DEL LABERINTO ---
    void TeleportPlayerToEntrance()
    {
        var cc = player.GetComponent<NavMeshAgent>();
        if (cc != null) cc.enabled = false;
        player.transform.position = entrancePos;
        if (cc != null) cc.enabled = true;
    }

    void WinGame()
    {
        gameWon = true;
        isRunning = false; // Detener el hilo de IA al ganar

        foreach (var agent in agents)
        {
            agent.isStopped = true;
        }

        winCanvas.SetActive(true);
    }

    public void RelocateAllNPC()
    {
        if (triangulation.vertices.Length == 0) return;

        // Limpiar acciones pendientes viejas antes de teletransportar
        lock (mainThreadActions)
        {
            mainThreadActions.Clear();
        }

        foreach (var agent in agents)
        {
            agent.enabled = false;
            agent.transform.position = GetValidRandmPosition();
            agent.enabled = true;
        }
    }

    public Vector3 GetValidRandmPosition()
    {
        Vector3 pos;
        do
        {
            int t = random.Next(0, triangulation.indices.Length / 3) * 3;
            Vector3 v1 = triangulation.vertices[triangulation.indices[t]];
            Vector3 v2 = triangulation.vertices[triangulation.indices[t + 1]];
            Vector3 v3 = triangulation.vertices[triangulation.indices[t + 2]];
            float r1 = (float)random.NextDouble();
            float r2 = (float)random.NextDouble();
            if (r1 + r2 > 1f)
            {
                r1 = 1f - r1;
                r2 = 1f - r2;
            }
            pos = v1 + r1 * (v2 - v1) + r2 * (v3 - v1);
        }
        while ((pos - entrancePos).sqrMagnitude < minDistanceSqr);
        return pos;
    }

    void FindAllEnemies()
    {
        agents.Clear();
        int priority = 50;

        foreach (var agent in FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None))
        {
            if (agent.CompareTag("Enemy"))
            {
                agent.avoidancePriority = Mathf.Clamp(priority, 0, 99);
                priority += 5; // Cada fantasma tiene distinta jerarquia para ceder el paso
                agents.Add(agent);
            }
        }
    }
}
