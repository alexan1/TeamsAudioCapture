using System;
using System.Net.Http;
using System.Threading.Tasks;

// Quick test to check available Gemini models
class TestGeminiModels
{
    static async Task Main()
    {
        Console.Write("Enter API key: ");
        var apiKey = Console.ReadLine();
        
        var client = new HttpClient();
        
        var url = $"https://generativelanguage.googleapis.com/v1/models?key={apiKey}";
        
        var response = await client.GetAsync(url);
        var content = await response.Content.ReadAsStringAsync();
        
        Console.WriteLine($"\nStatus: {response.StatusCode}");
        Console.WriteLine($"\nResponse:\n{content}");
    }
}
