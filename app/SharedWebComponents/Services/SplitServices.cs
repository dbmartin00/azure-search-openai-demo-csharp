public class SplitServices
{
    private static string s_trafficKey = Guid.NewGuid().ToString(); 

    public static string GetSplitSdkKey()
    {
        return "ep4lsb0sbbla53093p5jmmstgkf27c6osajo";
    }

    public static string GetEvaluatorURL()
    {
        return "https://split-evaluator.us.az.split-stage.io";
    }

    public static string GetSplitEvaluatorAuthKey()
    {
        return "splitInTheW1ld!";
    }

    public static string GetSplitTrafficKey()
    {
        return s_trafficKey;
    }
}
