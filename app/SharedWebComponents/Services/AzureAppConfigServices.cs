public class AzureAppConfigServices
{
    // private static string s_trafficKey = Guid.NewGuid().ToString(); 

    public static string GetAzureAppConfigConnectionString()
    {
        return "Endpoint=https://split-appconfig-demo.azconfig.io;Id=se+a;Secret=sWr/uJkqkKJZ21Y20TKgIUuV3OiN9CzTgrcmhiOlVGk=";
    }

    // public static string GetSplitTrafficKey()
    // {
    //     return s_trafficKey;
    // }
}