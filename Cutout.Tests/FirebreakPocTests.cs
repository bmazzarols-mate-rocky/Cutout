using System;
using System.Linq;
using Firebreak;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Cutout.Tests;

// Firebreak integration tests (ops-board#18). These prove the generator keeps
// the important properties:
//   1. a bad template produces a located diagnostic instead of AD0001
//   2. one bad template does not sink the good ones
//   3. Err payloads are value-equatable for incremental cache stability
//   4. the shared Firebreak helpers behave as expected
public class FirebreakPocTests
{
    private const string GoodTemplate = """
            [Template("hello {{value}}")]
            public static partial void Good(this StringBuilder builder, string value);
        """;

    private const string BadTemplate = """
            [Template("{{% unclosed")]
            public static partial void Bad(this StringBuilder builder, string value);
        """;

    private static GeneratorDriverRunResult Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "firebreak",
            new[] { CSharpSyntaxTree.ParseText(source) },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(
                    typeof(Cutout.TemplateAttribute).Assembly.Location
                ),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var driver = CSharpGeneratorDriver.Create(new Cutout.TemplateSourceGenerator());
        return driver.RunGenerators(compilation).GetRunResult();
    }

    // ------------------------------------------------------------------
    // 1. Bad template: located CUTOUT001 diagnostic, NOT an AD0001 crash.
    // ------------------------------------------------------------------
    [Fact]
    public void BadTemplateReportsLocatedDiagnosticInsteadOfCrash()
    {
        var result = Run(
            $$"""
            using System.Text;
            using Cutout;

            public class Sample
            {
                {{BadTemplate}}
            }
            """
        );

        var cutoutDiagnostic = result.Diagnostics.FirstOrDefault(d =>
            d.Id == "CUTOUT001" || d.Id == "CUTOUT002"
        );
        Assert.NotNull(cutoutDiagnostic);
        Assert.Equal(DiagnosticSeverity.Error, cutoutDiagnostic!.Severity);

        // The diagnostic points at the method declaration, not Location.None.
        Assert.NotEqual(Location.None, cutoutDiagnostic!.Location);
        Assert.True(cutoutDiagnostic!.Location.SourceSpan.Length > 0);

        // No generator crash anywhere.
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "AD0001");
    }

    // ------------------------------------------------------------------
    // 2. Isolation: the good template still generates when a sibling fails.
    // ------------------------------------------------------------------
    [Fact]
    public void GoodTemplateStillGeneratesWhenSiblingTemplateFails()
    {
        var result = Run(
            $$"""
            using System.Text;
            using Cutout;

            public class Sample
            {
                {{GoodTemplate}}
                {{BadTemplate}}
            }
            """
        );

        var goodTree = result.GeneratedTrees.FirstOrDefault(t =>
            t.ToString().Contains("hello ", StringComparison.Ordinal)
        );
        Assert.NotNull(goodTree);
        Assert.Contains("hello ", goodTree!.ToString());

        // No generator crash anywhere.
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "AD0001");
        Assert.Equal(1, result.Diagnostics.Count(d => d.Id == "CUTOUT001" || d.Id == "CUTOUT002"));
    }

    // ------------------------------------------------------------------
    // 3. Cache stability: Err payloads are pure data, not symbol references.
    // ------------------------------------------------------------------
    [Fact]
    public void ErrPayloadsAreValueEquatableForCacheStability()
    {
        var left = new Err<int>(
            EquatableArray<GenerationError>.Create(
                GenerationError.FromException(
                    FirebreakDescriptors.InternalError,
                    new InvalidOperationException("same"),
                    null
                )
            )
        );
        var right = new Err<int>(
            EquatableArray<GenerationError>.Create(
                GenerationError.FromException(
                    FirebreakDescriptors.InternalError,
                    new InvalidOperationException("same"),
                    null
                )
            )
        );

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    // ------------------------------------------------------------------
    // 4. Shared helper ergonomics.
    // ------------------------------------------------------------------
    [Fact]
    public void CaptureTurnsStageExceptionsIntoLocatedErr()
    {
        var tree = CSharpSyntaxTree.ParseText("class Sample { void Bad() {} }");
        var node = tree.GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .First();

        var result = FirebreakPipeline.Capture<int>(
            () => throw new InvalidOperationException("stage blew up"),
            node,
            FirebreakDescriptors.InternalError
        );

        Assert.True(result.IsErr);
        var err = (Err<int>)result;
        Assert.Equal(1, err.Errors.Count);
        Assert.Contains("stage blew up", err.Errors.First().Message);
        Assert.NotEqual(Location.None, err.Errors.First().ToDiagnostic().Location);
    }

    [Fact]
    public void ErrMapShortCircuitsWithoutRunningStage()
    {
        var error = GenerationError.At(FirebreakDescriptors.InternalError, "boom", null);
        var err = new Err<int>(EquatableArray<GenerationError>.Create(error));

        var ran = false;
        var mapped = err.Map<string>(value =>
        {
            ran = true;
            return "should never run";
        });

        Assert.False(ran);
        Assert.True(mapped.IsErr);
    }

    [Fact]
    public void PartitionKeepsGoodValuesAndCollectsBadOnes()
    {
        var tree = CSharpSyntaxTree.ParseText("class Sample { void Bad() {} }");
        var node = tree.GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .First();

        var partition = FirebreakPipeline.Partition(
            new Result<int>[]
            {
                new Ok<int>(1),
                FirebreakPipeline.Err<int>(
                    GenerationError.At(FirebreakDescriptors.InternalError, "bad", node)
                ),
                new Ok<int>(2),
            }
        );

        Assert.Equal(2, partition.Values.Length);
        Assert.Equal(1, partition.Errors.Count);
        Assert.Contains("bad", partition.Errors.First().Message);
    }
}
