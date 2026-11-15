namespace ManiDispatch.Evidencia;

// SCRUM-1681 (QAS-45). NO FUSIONAR.
// Codigo con issues a proposito para comprobar que el Quality Gate bloquea el pull request.
public class GateRojo
{
    private readonly int _sinUsar = 0;

    public int Suma(int a, int b)
    {
        var temporal = a * b;
        if (a > b)
        {
            return a + b;
        }
        else
        {
            return a + b;
        }
    }
}
