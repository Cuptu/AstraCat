using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.RegularExpressions;

namespace AstraCat.Tests;

[TestClass]
public class TerminologyExtractionTests
{
    [TestMethod]
    public void Extractor_DetectsCamelCaseAndEntities()
    {
        var sampleText = "We evaluated DeepSeek, ChatGPT, and OpenAI with the new LLM_MODEL_v2 pipeline.";
        var patterns = new[]
        {
            @"(?<![A-Za-z0-9])[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]+",
            @"(?<![A-Za-z0-9])[A-Za-z]+[0-9][A-Za-z0-9_]*",
            @"(?<![A-Za-z0-9])[a-z]+[A-Z][A-Za-z0-9_]*",
            @"(?<![\p{L}\p{N}])\p{Lu}[\p{L}\p{M}\p{N}_'’-]{2,}",
            @"(?<![\p{L}\p{N}])\p{Lu}[\p{L}\p{M}\p{N}_'’-]{2,}(?:\s+\p{Lu}[\p{L}\p{M}\p{N}_'’-]{2,}){0,3}",
        };

        var foundWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            foreach (Match match in Regex.Matches(sampleText, pattern))
            {
                foundWords.Add(match.Value);
            }
        }

        Assert.IsTrue(foundWords.Contains("DeepSeek"), "Should extract CamelCase DeepSeek");
        Assert.IsTrue(foundWords.Contains("ChatGPT"), "Should extract CamelCase ChatGPT");
        Assert.IsTrue(foundWords.Contains("OpenAI"), "Should extract CamelCase OpenAI");
        Assert.IsTrue(foundWords.Contains("LLM_MODEL_v2"), "Should extract snake_case/uppercase LLM_MODEL_v2");
    }
}
