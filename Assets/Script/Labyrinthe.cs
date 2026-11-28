using UnityEngine;
using System.Collections.Generic;
using UnityEngine.AI;
using System;
using System.Threading;

public class Labyrinthe : MonoBehaviour
{
    public enum GhostRole
    {
        Blinky,         // FantasmaRojo: Persecución directa
        Pinky,          // FantasmaRosa: Emboscada frontal
        Inky,           // FantasmaAzul: Flanqueo con Blinky
        Clyde,          // FantasmaAmarillo: Huye si está cerca
        ExitGuardian,   // FantasmaAzulF: Bloquea el camino a la salida
        RearGuard,      // FantasmaAzul2: Corta la retirada por la espalda
        SideFlanker,    // FantasmaBlanco1: Rodea por el costado
        Sentinel        // FantasmaBlanco2: Patrulla y caza si te acercas
    }

    [Header("Objetos en la escena")]
    public GameObject player;
    public Transform entrance;
    public Transform exit;
    public GameObject winCanvas;
    public Transform clydeCorner;

    [Header("Variables de configuracion")]
    public float detectionRange = 1f;
    public float exitRange = 1f;
    public float minDistanceFromEntrance = 3f;
    public int aiUpdateIntervalMs = 150;
    public float sentinelDetectionRange = 8f; // Radio de alerta para FantasmaBlanco2

    [Header("Separacion entre fantasmas")]
    public float separationRadius = 1.3f;   // Reducido para evitar rebotes en pasillos con 8 fantasmas
    public float separationDistance = 1.8f;

    // Agentes de IA
    List<NavMeshAgent> agents = new();
    GhostRole[] agentRoles;
    int blinkyIndex = 0;

    NavMeshTriangulation triangulation;
    Vector3 entrancePos;

    // Variables para el algoritmo
    float detectionRangeSqr;
    float exitRangeSqr;
    float minDistanceSqr;
    float sentinelRangeSqr;
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
    private Vector3 sharedExitPos;
    private Vector3[] sharedPatrolPoints;
    private int currentPatrolIndex = 0;

    System.Random mainRandom = new();

    void Start()
    {
        entrancePos = entrance.position;
        triangulation = NavMesh.CalculateTriangulation();

        detectionRangeSqr = detectionRange * detectionRange;
        exitRangeSqr = exitRange * exitRange;
        minDistanceSqr = minDistanceFromEntrance * minDistanceFromEntrance;
        sentinelRangeSqr = sentinelDetectionRange * sentinelDetectionRange;

        winCanvas.SetActive(false);

        FindAllEnemies();

        // Precalcular puntos de patrulla en el hilo principal (thread-safe)
        sharedPatrolPoints = new Vector3[6];
        for (int i = 0; i < sharedPatrolPoints.Length; i++)
        {
            sharedPatrolPoints[i] = GetValidRandmPosition();
        }

        // Inicializar estado compartido antes de arrancar el hilo
        sharedAgentPositions = new Vector3[agents.Count];
        sharedClydeCorner = clydeCorner != null ? clydeCorner.position : entrancePos;
        sharedExitPos = exit != null ? exit.position : entrancePos;
        UpdateSharedState();

        // Iniciar el hilo secundario de IA
        isRunning = true;
        aiThread = new Thread(GhostAILoop);
        aiThread.IsBackground = true;
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

        if (playerCaught)
        {
            TeleportPlayerToEntrance();
            RelocateAllNPC();
            return;
        }

        if ((playerPos - exit.position).sqrMagnitude < exitRangeSqr)
        {
            WinGame();
            return;
        }

        UpdateSharedState();

        lock (mainThreadActions)
        {
            while (mainThreadActions.Count > 0)
            {
                mainThreadActions.Dequeue().Invoke();
            }
        }
    }

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

 
   //HILO SECUNDARIO: CEREBRO DE LOS FANTASMAS 
    private void GhostAILoop()
    {
        Vector3[] lastPositions = new Vector3[agents.Count];
        int[] stuckTicks = new int[agents.Count];
        int[] unstuckBoostTicks = new int[agents.Count];

        while (isRunning)
        {
            Thread.Sleep(aiUpdateIntervalMs);

            if (gameWon) continue;

            Vector3 pPos;
            Vector3 pFwd;
            Vector3[] aPos;
            Vector3 corner;
            Vector3 exitPos;

            lock (stateLock)
            {
                if (sharedAgentPositions == null || sharedAgentPositions.Length == 0) continue;

                pPos = sharedPlayerPos;
                pFwd = sharedPlayerForward;
                aPos = (Vector3[])sharedAgentPositions.Clone();
                corner = sharedClydeCorner;
                exitPos = sharedExitPos;
            }

            for (int i = 0; i < aPos.Length; i++)
            {
                int ghostIndex = i;

                // 1. Detector de atasco (menos de 0.15m movidos en un tick)
                if ((aPos[i] - lastPositions[i]).sqrMagnitude < 0.0225f)
                {
                    stuckTicks[i]++;
                    if (stuckTicks[i] >= 5) // ~750 ms quieto
                    {
                        unstuckBoostTicks[i] = 8; // ~1.2 s en modo destrabe
                        stuckTicks[i] = 0;

                        if (agentRoles[i] == GhostRole.Sentinel)
                        {
                            currentPatrolIndex = (currentPatrolIndex + 1) % sharedPatrolPoints.Length;
                        }
                    }
                }
                else
                {
                    stuckTicks[i] = 0;
                }
                lastPositions[i] = aPos[i];

                // 2. Calcular destino (si está en modo destrabe, va directo al jugador)
                Vector3 targetDestination;
                if (unstuckBoostTicks[i] > 0)
                {
                    unstuckBoostTicks[i]--;
                    targetDestination = pPos;
                }
                else
                {
                    targetDestination = CalculateGhostTarget(ghostIndex, pPos, pFwd, aPos, corner, exitPos);
                }

                Vector3 fallbackPlayerPos = pPos;

                lock (mainThreadActions)
                {
                    mainThreadActions.Enqueue(() =>
                    {
                        if (ghostIndex < agents.Count && agents[ghostIndex] != null &&
                            agents[ghostIndex].enabled && !agents[ghostIndex].isStopped)
                        {
                            if (NavMesh.SamplePosition(targetDestination, out NavMeshHit hit, 3.0f, NavMesh.AllAreas))
                            {
                                agents[ghostIndex].SetDestination(hit.position);
                            }
                            else
                            {
                                // Fallback seguro: si el objetivo cayó fuera del NavMesh, ir al jugador
                                agents[ghostIndex].SetDestination(fallbackPlayerPos);
                            }
                        }
                    });
                }
            }
        }
    }

    private Vector3 CalculateGhostTarget(int index, Vector3 playerPos, Vector3 playerFwd, Vector3[] agentPositions, Vector3 corner, Vector3 exitPos)
    {
        Vector3 roleTarget;

        switch (agentRoles[index])
        {
            case GhostRole.Blinky:
                roleTarget = playerPos;
                break;

            case GhostRole.Pinky:
                roleTarget = playerPos + (playerFwd * 4f);
                break;

            case GhostRole.Inky:
                Vector3 pivot = playerPos + (playerFwd * 2f);
                Vector3 blinkyPos = agentPositions[blinkyIndex];
                roleTarget = pivot + (pivot - blinkyPos);
                break;

            case GhostRole.Clyde:
                float distSqr = (agentPositions[index] - playerPos).sqrMagnitude;
                roleTarget = (distSqr > 36f) ? playerPos : corner;
                break;

            case GhostRole.ExitGuardian:
                roleTarget = Vector3.Lerp(exitPos, playerPos, 0.35f);
                break;

            case GhostRole.RearGuard:
                roleTarget = playerPos - (playerFwd * 3.5f);
                break;

            case GhostRole.SideFlanker:
                Vector3 playerRight = Vector3.Cross(Vector3.up, playerFwd).normalized;
                roleTarget = playerPos + (playerRight * 4f);
                break;

            case GhostRole.Sentinel:
                if ((agentPositions[index] - playerPos).sqrMagnitude < sentinelRangeSqr)
                {
                    roleTarget = playerPos;
                }
                else
                {
                    Vector3 patrolTarget = sharedPatrolPoints[currentPatrolIndex];
                    if ((agentPositions[index] - patrolTarget).sqrMagnitude < 4f)
                    {
                        currentPatrolIndex = (currentPatrolIndex + 1) % sharedPatrolPoints.Length;
                        patrolTarget = sharedPatrolPoints[currentPatrolIndex];
                    }
                    roleTarget = patrolTarget;
                }
                break;

            default:
                roleTarget = playerPos;
                break;
        }

        // Combinar separación suave con la dirección del objetivo en vez de anularla
        Vector3 repulsion = CalculateSeparation(index, agentPositions);
        if (repulsion != Vector3.zero)
        {
            Vector3 toTarget = (roleTarget - agentPositions[index]).normalized;
            Vector3 blendedDir = (toTarget + repulsion.normalized * 1.2f).normalized;
            return agentPositions[index] + (blendedDir * separationDistance);
        }

        return roleTarget;
    }

    private Vector3 CalculateSeparation(int index, Vector3[] agentPositions)
    {
        Vector3 myPos = agentPositions[index];
        Vector3 repulsion = Vector3.zero;
        float radiusSqr = separationRadius * separationRadius;

        for (int j = 0; j < agentPositions.Length; j++)
        {
            if (j == index) continue;

            Vector3 diff = myPos - agentPositions[j];
            diff.y = 0f;

            float sqrDist = diff.sqrMagnitude;
            if (sqrDist < radiusSqr)
            {
                if (sqrDist < 0.0001f)
                {
                    float angle = index * 0.785f; // 45 grados por índice (cubre 8 fantasmas sin repetirse)
                    repulsion += new Vector3((float)Math.Cos(angle), 0f, (float)Math.Sin(angle));
                }
                else
                {
                    repulsion += diff.normalized / (float)Math.Sqrt(sqrDist);
                }
            }
        }

        return repulsion;
    }

    void OnDestroy()
    {
        isRunning = false;
        if (aiThread != null && aiThread.IsAlive)
        {
            aiThread.Join(100);
        }
    }

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
        isRunning = false;

        foreach (var agent in agents)
        {
            agent.isStopped = true;
        }

        winCanvas.SetActive(true);
    }

    public void RelocateAllNPC()
    {
        if (triangulation.vertices.Length == 0) return;

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
            int t = mainRandom.Next(0, triangulation.indices.Length / 3) * 3;
            Vector3 v1 = triangulation.vertices[triangulation.indices[t]];
            Vector3 v2 = triangulation.vertices[triangulation.indices[t + 1]];
            Vector3 v3 = triangulation.vertices[triangulation.indices[t + 2]];
            float r1 = (float)mainRandom.NextDouble();
            float r2 = (float)mainRandom.NextDouble();
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
        List<GhostRole> rolesList = new();
        int priority = 20;

        foreach (var agent in FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None))
        {
            if (!agent.CompareTag("Enemy")) continue;

            agent.avoidancePriority = Mathf.Clamp(priority, 0, 99);
            priority += 5;

            GhostRole role = AssignRoleByName(agent.name, rolesList.Count);
            if (role == GhostRole.Blinky)
            {
                blinkyIndex = agents.Count;
            }

            agents.Add(agent);
            rolesList.Add(role);
        }

        agentRoles = rolesList.ToArray();
    }

    private GhostRole AssignRoleByName(string ghostName, int fallbackIndex)
    {
        switch (ghostName)
        {
            case "FantasmaRojo": return GhostRole.Blinky;
            case "FantasmaRosa": return GhostRole.Pinky;
            case "FantasmaAzul": return GhostRole.Inky;
            case "FantasmaAmarillo": return GhostRole.Clyde;
            case "FantasmaAzulF": return GhostRole.ExitGuardian;
            case "FantasmaAzul2": return GhostRole.RearGuard;
            case "FantasmaBlanco1": return GhostRole.SideFlanker;
            case "FantasmaBlanco2": return GhostRole.Sentinel;
            default: return (GhostRole)(fallbackIndex % 8);
        }
    }
}