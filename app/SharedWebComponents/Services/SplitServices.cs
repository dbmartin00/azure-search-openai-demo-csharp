public class SplitServices
{
    private static string s_trafficKey = Guid.NewGuid().ToString(); 

    public static string GetSplitSdkKey()
    {
        return "28bddhnjht06lvi8e5aa9rkmv5glsc40ltaa";
    }

    public static string GetEvaluatorURL()
    {
        return "https://split-evaluator.darkbloom.org";
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
