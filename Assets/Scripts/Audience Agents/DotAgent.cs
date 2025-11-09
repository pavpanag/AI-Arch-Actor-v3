using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class DotAgent : MonoBehaviour
{
    public float minIdleTime = 2f;
    public float maxIdleTime = 6f;
    public float areaSize = 10f;

    private NavMeshAgent agent;
    private float idleTimer;
    private enum State { Idle, Moving }
    private State currentState;

    void Start()
    {
        agent = GetComponent<NavMeshAgent>();
        EnterIdle();
    }

    void Update()
    {
        if (currentState == State.Idle)
        {
            idleTimer -= Time.deltaTime;
            if (idleTimer <= 0f)
            {
                PickNewTarget();
                currentState = State.Moving;
            }
        }
        else if (currentState == State.Moving)
        {
            if (!agent.pathPending && agent.remainingDistance < 0.2f)
            {
                EnterIdle();
            }
        }
    }

    void PickNewTarget()
    {
        // Διάλεξε τυχαίο σημείο μέσα στο area
        Vector3 randomPos = new Vector3(
            Random.Range(-areaSize, areaSize),
            0f,
            Random.Range(-areaSize, areaSize)
        );

        NavMeshHit hit;
        if (NavMesh.SamplePosition(randomPos, out hit, 2f, NavMesh.AllAreas))
        {
            agent.SetDestination(hit.position);
        }
    }

    void EnterIdle()
    {
        currentState = State.Idle;
        idleTimer = Random.Range(minIdleTime, maxIdleTime);
        agent.ResetPath();
    }
}
