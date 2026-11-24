using System.CodeDom.Compiler;
using System.Collections.Immutable;
using System.Text;
using Cutout.Exceptions;
using Firebreak;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Pasted;
using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Cutout;

/// <summary>
/// Source generator for Cutout templates
/// </summary>
#pragma warning disable RS1038
[Generator]
#pragma warning restore RS1038
public sealed partial class TemplateSourceGenerator : IIncrementalGenerator
{
#pragma warning disable RS2008
    private static readonly DiagnosticDescriptor TemplateParseError = new DiagnosticDescriptor(
        "CUTOUT001",
        "Template parsing error",
        "Failed to parse template for {0}: {1}",
        "Cutout",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor TemplateRenderError = new DiagnosticDescriptor(
        "CUTOUT002",
        "Template rendering error",
        "Failed to render template for {0}: {1}",
        "Cutout",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
#pragma warning restore RS2008

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(ctx =>
        {
            ctx.AddSource("TemplateAttribute.g", EmbeddedFiles.TemplateAttribute_Source);
            ctx.AddSource("FileTemplateAttribute.g", EmbeddedFiles.FileTemplateAttribute_Source);
            ctx.AddSource("RenderUtilities.g", EmbeddedFiles.RenderUtilities_Source);
        });

        var attributeTemplateProvider = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Cutout.TemplateAttribute",
            IsTemplateMethod,
            BuildTemplateDetails
        );

        var attributeTemplates = attributeTemplateProvider;

        context.RegisterSourceOutput(attributeTemplates, GenerateTemplate);

        // external config-based template methods
        var embeddedFiles = context.AdditionalTextsProvider.Select((file, _) => file).Collect();
        var configProvider = context.AnalyzerConfigOptionsProvider.Select((options, _) => options);
        var fileTemplateAttributeProvider = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                "Cutout.FileTemplateAttribute",
                IsTemplateMethod,
                (x, _) => x
            )
            .Collect();

        var fileTemplates = embeddedFiles
            .Combine(configProvider)
            .Combine(fileTemplateAttributeProvider)
            .SelectMany(BuildTemplateDetailsFromFile);

        context.RegisterSourceOutput(fileTemplates, GenerateTemplate);
    }

    private static bool IsTemplateMethod(SyntaxNode syntax, CancellationToken token)
    {
        return syntax is MethodDeclarationSyntax mds
            // the method is either static
            && mds.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
            // and returns a void
            && mds.ReturnType is PredefinedTypeSyntax pts
            && pts.Keyword.IsKind(SyntaxKind.VoidKeyword)
            // and has at least one this parameter
            && mds.ParameterList.Parameters.Any(p =>
                p.Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword))
            );
    }

    private static IEnumerable<Result<TemplateMethodDetails>> BuildTemplateDetailsFromFile(
        (
            (ImmutableArray<AdditionalText> Files, AnalyzerConfigOptionsProvider Options) Static,
            ImmutableArray<GeneratorAttributeSyntaxContext> Methods
        ) ctx,
        CancellationToken token
    )
    {
        Dictionary<
            string,
            (
                SemanticModel semanticModel,
                MethodDeclarationSyntax methodDeclarationSyntax,
                IMethodSymbol methodSymbol
            )
        > methodLookup = new(StringComparer.Ordinal);
        foreach (var context in ctx.Methods)
        {
            if (context.TargetNode is not MethodDeclarationSyntax mds)
                continue;

            var methodSymbol = context.SemanticModel.GetDeclaredSymbol(mds, token);

            if (methodSymbol is null)
                continue;

            var ns = methodSymbol.ContainingNamespace?.ToDisplayString();
            var @class = methodSymbol.ContainingType.Name;
            var fullName = $"{ns}.{@class}.{methodSymbol.Name}";
            methodLookup.Add(fullName, (context.SemanticModel, mds, methodSymbol));
        }

        foreach (var file in ctx.Static.Files)
        {
            if (file.GetText(token)?.ToString() is not { } templateText)
            {
                continue;
            }

            var options = ctx.Static.Options.GetOptions(file);

            if (
                !options.TryGetValue("template_method", out var method)
                || string.IsNullOrWhiteSpace(method)
            )
            {
                continue;
            }

            if (!methodLookup.TryGetValue(method, out var methodContext))
            {
                continue;
            }

            // Firebreak POC: per-file failures become Err values, located at the
            // method declaration — a broken file template never takes down the lane.
            Result<TemplateMethodDetails> entry;
            try
            {
                entry = BuildTemplateDetailsFromSyntax(
                    methodContext.semanticModel,
                    methodContext.methodDeclarationSyntax,
                    methodContext.methodSymbol,
                    templateText
                );
            }
            catch (Exception ex)
            {
                entry = FirebreakPipeline.Err<TemplateMethodDetails>(
                    GenerationError.FromException(
                        TemplateParseError,
                        ex,
                        methodContext.methodDeclarationSyntax
                    )
                );
            }

            yield return entry;
        }
    }

    private static Result<TemplateMethodDetails> BuildTemplateDetails(
        GeneratorAttributeSyntaxContext ctx,
        CancellationToken token
    )
    {
        try
        {
            var methodDeclarationSyntax = (MethodDeclarationSyntax)ctx.TargetNode;

            var methodSymbol = ctx.SemanticModel.GetDeclaredSymbol(
                methodDeclarationSyntax,
                cancellationToken: token
            )!;

            return BuildTemplateDetailsFromSyntax(
                ctx.SemanticModel,
                methodDeclarationSyntax,
                methodSymbol,
                template: null
            );
        }
        catch (Exception ex)
        {
            return FirebreakPipeline.Err<TemplateMethodDetails>(
                GenerationError.FromException(TemplateParseError, ex, ctx.TargetNode)
            );
        }
    }

    private static TemplateMethodDetails BuildTemplateDetailsFromSyntax(
        SemanticModel semanticModel,
        MethodDeclarationSyntax methodDeclarationSyntax,
        IMethodSymbol methodSymbol,
        string? template
    )
    {
        var ns = methodSymbol.ContainingNamespace?.ToDisplayString();

        // get all usings
        var usings = methodDeclarationSyntax
            .TryGetUsings()
            .Add(SF.UsingDirective(SF.ParseName(" System")))
            .Add(SF.UsingDirective(SF.ParseName(" System.Diagnostics.CodeAnalysis")))
            .Add(SF.UsingDirective(SF.ParseName(" Cutout")));

        // get the method details
        var containingType = methodSymbol.ContainingType;
        var @class = containingType.Name;
        var classDetails = new ClassDetails(Name: @class, ClassSymbol: containingType);

        var name = methodDeclarationSyntax.Identifier.Text;
        var methodDetails = new MethodDetails(
            Name: name,
            MethodDeclaration: methodDeclarationSyntax,
            MethodSymbol: methodSymbol
        );

        // get the attribute details
        var attributeParts =
            template is null || string.IsNullOrWhiteSpace(template)
                ? new TemplateAttributeParts(methodDetails, semanticModel)
                : new TemplateAttributeParts(template);

        return new TemplateMethodDetails(
            Namespace: ns,
            Usings: usings,
            ClassDetails: classDetails,
            MethodDetails: methodDetails,
            AttributeDetails: attributeParts
        );
    }

    private static void GenerateTemplate(
        SourceProductionContext context,
        Result<TemplateMethodDetails> result
    )
    {
        context.ReportDiagnostics(result);

        if (result is Err<TemplateMethodDetails>)
        {
            return;
        }

        var details = ((Ok<TemplateMethodDetails>)result).Value;

        var rendered = FirebreakPipeline.Capture(
            () => RenderTemplateOutputs(details),
            details.MethodDetails.MethodDeclaration,
            TemplateRenderError
        );

        if (rendered is Ok<ImmutableArray<GeneratedSource>> ok)
        {
            foreach (var output in ok.Value)
            {
                context.AddSource(output.HintName, SourceText.From(output.Content, Encoding.UTF8));
            }
        }
        else if (rendered is Err<ImmutableArray<GeneratedSource>> error)
        {
            foreach (var diagnostic in error.ToDiagnostics())
            {
                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    private sealed record GeneratedSource(string HintName, string Content);

    private static ImmutableArray<GeneratedSource> RenderTemplateOutputs(
        TemplateMethodDetails details
    )
    {
        var outputs = new GeneratedSource[]
        {
            RenderTemplateMethod(details, includeWhitespaceReceiver: false, hintSuffix: ".g.cs"),
            RenderTemplateMethod(details, includeWhitespaceReceiver: true, hintSuffix: ".wsr.g.cs"),
        };

        return outputs.ToImmutableArray();
    }

    private static GeneratedSource RenderTemplateMethod(
        TemplateMethodDetails details,
        bool includeWhitespaceReceiver,
        string hintSuffix
    )
    {
        using (var writer = new StringWriter())
        {
            var indentedWriter = new IndentedTextWriter(writer, "    ");
            WriteTemplateMethod(indentedWriter, details, includeWhitespaceReceiver);
            return new GeneratedSource(
                $"{details.ClassDetails.Name}.{details.MethodDetails.Name}{hintSuffix}",
                writer.ToString()
            );
        }
    }
}
