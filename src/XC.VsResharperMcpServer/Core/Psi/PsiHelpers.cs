using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.CSharp.Util;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util.dataStructures.TypedIntrinsics;

namespace XC.VsResharperMcpServer.Core.Psi
{
    // Ported near-verbatim from resharper-mcp (Rider) — see docs/DEVNOTES.md.
    // Host-agnostic: depends only on shared ReSharper Platform/PSI types.
    public static class PsiHelpers
    {
        public const int MaxSnippetLength = 2000;

        public class FileResolveResult
        {
            public IPsiSourceFile SourceFile { get; set; }
            public IProjectFile ProjectFile { get; set; }
            public string Error { get; set; }

            public bool IsFound => SourceFile != null;
        }

        public static IPsiSourceFile GetSourceFile(ISolution solution, string filePath)
        {
            return ResolveFile(solution, filePath).SourceFile;
        }

        public static FileResolveResult ResolveFile(ISolution solution, string filePath)
        {
            // Unlike GetSolutionStructureTool, this used to enumerate ALL of solution.GetAllProjects()
            // unfiltered - including virtual/miscellaneous-files pseudo-projects with no real
            // ProjectFileLocation, whose GetAllProjectFiles() can throw. Matches the filter
            // GetSolutionStructureTool already applies. See docs/DEVNOTES.md M7 combined-test round.
            var allFilesQuery = solution.GetAllProjects()
                .Where(p => p.ProjectFileLocation != null && !p.ProjectFileLocation.IsEmpty)
                .SelectMany(p => p.GetAllProjectFiles());

            IProjectFile projectFile = null;

            projectFile = allFilesQuery.FirstOrDefault(f => f.Location.FullPath == filePath);

            if (projectFile == null && !Path.IsPathRooted(filePath))
            {
                var solutionDir = solution.SolutionFilePath?.Directory?.FullPath;
                if (solutionDir != null)
                {
                    var resolved = Path.GetFullPath(Path.Combine(solutionDir, filePath));
                    projectFile = allFilesQuery.FirstOrDefault(f => f.Location.FullPath == resolved);
                }
            }

            if (projectFile == null)
            {
                projectFile = allFilesQuery.FirstOrDefault(f =>
                    string.Equals(f.Location.FullPath, filePath, StringComparison.OrdinalIgnoreCase));
            }

            if (projectFile == null)
            {
                var suffix = filePath.Replace('\\', '/');
                projectFile = allFilesQuery.FirstOrDefault(f =>
                {
                    var full = f.Location.FullPath.Replace('\\', '/');
                    return full.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                        && (full.Length == suffix.Length || full[full.Length - suffix.Length - 1] == '/');
                });
            }

            if (projectFile == null)
                return new FileResolveResult
                {
                    Error = $"File not found in solution: {filePath}"
                };

            var sourceFile = projectFile.ToSourceFiles().FirstOrDefault();
            if (sourceFile != null)
                return new FileResolveResult { SourceFile = sourceFile, ProjectFile = projectFile };

            var targetPath = projectFile.Location.FullPath;
            var psiServices = solution.GetPsiServices();
            foreach (var project in solution.GetAllProjects())
            {
                foreach (var module in psiServices.Modules.GetPsiModules(project))
                {
                    foreach (var sf in module.SourceFiles)
                    {
                        if (sf.GetLocation().FullPath == targetPath)
                            return new FileResolveResult { SourceFile = sf, ProjectFile = projectFile };
                    }
                }
            }

            return new FileResolveResult
            {
                ProjectFile = projectFile,
                Error = $"File exists in project but PSI source is not available (index may be stale): {projectFile.Location.FullPath}"
            };
        }

        public class SymbolResolveResult
        {
            public IDeclaredElement Element { get; set; }
            public List<SymbolCandidate> Candidates { get; set; }
            public bool IsAmbiguous => Candidates != null && Candidates.Count > 1;
            public bool IsFound => Element != null;
        }

        public class SymbolCandidate
        {
            public string Name { get; set; }
            public string QualifiedName { get; set; }
            public string Kind { get; set; }
            public string File { get; set; }
            public int Line { get; set; }
        }

        public static SymbolResolveResult ResolveSymbolByName(ISolution solution, string symbolName, string kind = null)
        {
            if (string.IsNullOrEmpty(symbolName))
                return new SymbolResolveResult();

            var nameParts = symbolName.Split('.');
            var shortName = nameParts[nameParts.Length - 1];
            var isQualified = nameParts.Length > 1;

            var psiServices = solution.GetPsiServices();
            var symbolScope = psiServices.Symbols
                .GetSymbolScope(LibrarySymbolScope.NONE, caseSensitive: true);

            var candidates = new List<(IDeclaredElement element, string fqn)>();
            var seenFqns = new HashSet<string>();

            foreach (var element in symbolScope.GetElementsByShortName(shortName))
            {
                if (element is INamespace) continue;

                if (kind != null && !MatchesKind(element, kind)) continue;

                var fqn = GetQualifiedName(element);

                if (isQualified && !FqnEndsWith(fqn, symbolName)) continue;

                if (!seenFqns.Add(fqn)) continue;

                candidates.Add((element, fqn));
            }

            if (candidates.Count == 0 && (kind == null || kind == "method" || kind == "property" || kind == "field" || kind == "event"))
            {
                if (isQualified && nameParts.Length >= 2)
                {
                    var containingTypeName = nameParts[nameParts.Length - 2];
                    foreach (var typeElement in symbolScope.GetElementsByShortName(containingTypeName))
                    {
                        if (!(typeElement is ITypeElement te)) continue;

                        foreach (var member in te.GetMembers())
                        {
                            if (member.ShortName != shortName) continue;
                            if (kind != null && !MatchesKind(member, kind)) continue;

                            var fqn = GetQualifiedName(member);
                            if (isQualified && !FqnEndsWith(fqn, symbolName)) continue;

                            if (!seenFqns.Add(fqn)) continue;
                            candidates.Add((member, fqn));
                        }
                    }
                }
                else
                {
                    foreach (var typeName in symbolScope.GetAllShortNames())
                    {
                        foreach (var element in symbolScope.GetElementsByShortName(typeName))
                        {
                            if (!(element is ITypeElement te)) continue;

                            foreach (var member in te.GetMembers())
                            {
                                if (member.ShortName != shortName) continue;
                                if (kind != null && !MatchesKind(member, kind)) continue;

                                var fqn = GetQualifiedName(member);
                                if (!seenFqns.Add(fqn)) continue;
                                candidates.Add((member, fqn));
                            }
                        }

                        if (candidates.Count > 0 && candidates.Count >= 10) break;
                    }
                }
            }

            if (candidates.Count == 0 && isQualified && (kind == null || kind == "method"))
            {
                var containingTypeName = nameParts.Length >= 3
                    ? nameParts[nameParts.Length - 3]
                    : nameParts[nameParts.Length - 2];
                var containingMethodName = nameParts.Length >= 3
                    ? nameParts[nameParts.Length - 2]
                    : null;

                foreach (var typeElement in symbolScope.GetElementsByShortName(containingTypeName))
                {
                    if (!(typeElement is ITypeElement te)) continue;

                    foreach (var member in te.GetMembers())
                    {
                        if (containingMethodName != null && member.ShortName != containingMethodName)
                            continue;

                        foreach (var decl in member.GetDeclarations())
                        {
                            foreach (var lfd in FindAllLocalFunctions(decl))
                            {
                                if (lfd.DeclaredName != shortName) continue;
                                var el = lfd.DeclaredElement;
                                if (el == null) continue;
                                var fqn = te.ShortName + "." + member.ShortName + "." + shortName;
                                if (!seenFqns.Add(fqn)) continue;
                                candidates.Add((el, fqn));
                            }
                        }
                    }
                }
            }

            if (candidates.Count == 0)
                return new SymbolResolveResult();

            if (candidates.Count == 1)
                return new SymbolResolveResult { Element = candidates[0].element };

            var candidateInfos = new List<SymbolCandidate>();
            foreach (var (element, fqn) in candidates)
            {
                string filePath = null;
                var line = 0;
                foreach (var d in element.GetDeclarations())
                {
                    var s = d.GetSourceFile();
                    var path = s?.GetLocation().FullPath;
                    if (!string.IsNullOrEmpty(path))
                    {
                        filePath = path;
                        var range = TreeNodeExtensions.GetDocumentRange(d);
                        if (range.IsValid())
                        {
                            var (l, _) = GetLineColumn(range.StartOffset);
                            line = l;
                        }
                        break;
                    }
                }

                candidateInfos.Add(new SymbolCandidate
                {
                    Name = element.ShortName,
                    QualifiedName = fqn,
                    Kind = element.GetElementType().PresentableName,
                    File = filePath ?? "[no source]",
                    Line = line
                });
            }

            return new SymbolResolveResult { Candidates = candidateInfos };
        }

        private static bool FqnEndsWith(string fqn, string suffix)
        {
            if (fqn == suffix) return true;
            return fqn.Length > suffix.Length
                && fqn.EndsWith(suffix)
                && fqn[fqn.Length - suffix.Length - 1] == '.';
        }

        private static bool MatchesKind(IDeclaredElement element, string kind)
        {
            switch (kind.ToLowerInvariant())
            {
                case "type": return element is ITypeElement;
                case "method": return element is IMethod ||
                    element is JetBrains.ReSharper.Psi.CSharp.DeclaredElements.ILocalFunction;
                case "property": return element is IProperty;
                case "field": return element is IField;
                case "event": return element is IEvent;
                default: return false;
            }
        }

        private static IEnumerable<ILocalFunctionDeclaration> FindAllLocalFunctions(ITreeNode node)
        {
            for (var child = node.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is ILocalFunctionDeclaration lfd)
                {
                    yield return lfd;
                    foreach (var nested in FindAllLocalFunctions(lfd))
                        yield return nested;
                }
                else
                {
                    foreach (var found in FindAllLocalFunctions(child))
                        yield return found;
                }
            }
        }

        public static string GetQualifiedName(IDeclaredElement element)
        {
            var parts = new List<string> { element.ShortName };

            if (element is IClrDeclaredElement clr)
            {
                var containingType = clr.GetContainingType();
                while (containingType != null)
                {
                    parts.Insert(0, containingType.ShortName);
                    containingType = containingType.GetContainingType();
                }

                var ns = clr is ITypeElement te
                    ? te.GetContainingNamespace()
                    : clr.GetContainingType()?.GetContainingNamespace();

                if (ns != null && !string.IsNullOrEmpty(ns.QualifiedName))
                    parts.Insert(0, ns.QualifiedName);
            }

            return string.Join(".", parts);
        }

        public static (IDeclaredElement element, string error) ResolveFromArgs(
            ISolution solution, string symbolName, string kind, string filePath, int line, int column)
        {
            if (!string.IsNullOrEmpty(symbolName))
            {
                var result = ResolveSymbolByName(solution, symbolName, kind);

                if (result.IsAmbiguous)
                {
                    var candidates = string.Join("; ", result.Candidates.Select(c =>
                        $"{c.QualifiedName} ({c.Kind}) — {c.File}:{c.Line}"));
                    return (null,
                        $"Ambiguous symbol '{symbolName}': found {result.Candidates.Count} matches. " +
                        "Use a qualified name, add 'kind' filter, or use filePath+line+column instead. " +
                        $"Candidates: {candidates}");
                }

                if (!result.IsFound)
                {
                    if (kind != null)
                    {
                        var withoutKind = ResolveSymbolByName(solution, symbolName, null);
                        if (withoutKind.IsFound)
                        {
                            var actualKind = withoutKind.Element.GetElementType().PresentableName;
                            return (null, $"No {kind} named '{symbolName}' found. Did you mean the {actualKind} '{symbolName}'?");
                        }
                        if (withoutKind.IsAmbiguous)
                        {
                            var kinds = string.Join(", ", withoutKind.Candidates.Select(c => c.Kind).Distinct());
                            return (null, $"No {kind} named '{symbolName}' found. Found symbols with that name of kind(s): {kinds}");
                        }
                    }
                    return (null, $"Symbol not found: {symbolName}");
                }

                return (result.Element, null);
            }

            if (!string.IsNullOrEmpty(filePath) && line > 0 && column > 0)
            {
                var resolved = ResolveFile(solution, filePath);
                if (!resolved.IsFound)
                    return (null, resolved.Error);
                var sourceFile = resolved.SourceFile;

                var node = GetNodeAtPosition(sourceFile, line, column);
                if (node == null)
                    return (null, $"No syntax node found at {line}:{column}");

                var element = GetDeclaredElement(node);
                if (element == null)
                {
                    var refName = GetNearestReferenceName(node);
                    if (refName != null)
                        return (null, $"Cannot resolve symbol '{refName}' at {line}:{column}. It may be from an external/compiled assembly.");
                    return (null, $"No resolvable symbol found at {line}:{column}");
                }

                return (element, null);
            }

            return (null, "Provide either 'symbolName' or 'filePath'+'line'+'column'");
        }

        public static IFile GetPsiFile(IPsiSourceFile sourceFile)
        {
            var psiFiles = sourceFile.GetPsiFiles<KnownLanguage>();
            foreach (var psiFile in psiFiles)
                return psiFile;

            return null;
        }

        public static ITreeNode GetNodeAtPosition(IPsiSourceFile sourceFile, int line, int column)
        {
            var psiFile = GetPsiFile(sourceFile);
            if (psiFile == null) return null;

            var document = sourceFile.Document;
            var docLine = (Int32<DocLine>)(line - 1);
            var docColumn = (Int32<DocColumn>)(column - 1);
            var coords = new DocumentCoords(docLine, docColumn);
            var offset = document.GetOffsetByCoords(coords);
            var treeOffset = psiFile.Translate(new DocumentOffset(document, offset));

            return psiFile.FindNodeAt(treeOffset);
        }

        public static IDeclaredElement GetDeclaredElement(ITreeNode node)
        {
            var current = node;
            for (var depth = 0; current != null && depth < 5; depth++)
            {
                foreach (var reference in current.GetReferences())
                {
                    var resolved = reference.Resolve();
                    if (resolved.DeclaredElement != null)
                        return resolved.DeclaredElement;
                }
                current = current.Parent;
            }

            current = node;
            for (var depth = 0; current != null && depth < 3; depth++)
            {
                if (current is IDeclaration declaration)
                    return declaration.DeclaredElement;
                current = current.Parent;
            }

            return null;
        }

        private static string GetNearestReferenceName(ITreeNode node)
        {
            var current = node;
            for (int i = 0; i < 3 && current != null; i++)
            {
                foreach (var r in current.GetReferences())
                    return r.GetName();
                current = current.Parent;
            }
            var text = node.GetText()?.Trim();
            return !string.IsNullOrEmpty(text) && text.Length <= 100 ? text : null;
        }

        public static (int line, int column) GetLineColumn(DocumentOffset offset)
        {
            var coords = offset.ToDocumentCoords();
            return ((int)coords.Line + 1, (int)coords.Column + 1);
        }

        // Explicitly flushes a mutated in-memory document to its backing file. Originally added
        // because apply_quick_fix/apply_suggestions mutate an IDocument via a synthetic ITextControl
        // or an IModernManualScopedAction (neither goes through a PsiTransactionCookie the way
        // rename_symbol/generate_members/format_file do) and never write to disk on their own in a
        // headless host. PsiTransactionCookie-based tools are NOT exempt from this either, though:
        // live-verified 2026-07-13 that rename_symbol's cascading rename correctly updates the
        // IDocument for a file already open in a live VS editor tab (the editor shows the new name
        // immediately, tab marked dirty) but does NOT flush that file to disk - only files with no
        // open editor got auto-persisted by the transaction commit. Any PSI-transaction-based write
        // tool touching multiple files should call this for every changed file, not just assume the
        // transaction's own commit reaches disk. Preserves the original file's BOM/no-BOM choice so
        // this doesn't introduce a spurious encoding-only diff.
        public static void PersistDocumentToDisk(string filePath, string text)
        {
            var hasBom = false;
            try
            {
                if (File.Exists(filePath))
                {
                    var head = new byte[3];
                    using (var stream = File.OpenRead(filePath))
                    {
                        var read = stream.Read(head, 0, 3);
                        hasBom = read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
                    }
                }
            }
            catch
            {
                // Best-effort BOM detection; fall back to no-BOM UTF-8 if we can't read the original.
            }

            File.WriteAllText(filePath, text, new UTF8Encoding(hasBom));
        }

        public static string TruncateSnippet(string text, int maxLength = MaxSnippetLength)
        {
            if (text == null) return null;
            text = text.Trim();
            if (text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "...";
        }

        public static string FormatSignature(IDeclaredElement element)
        {
            var lang = element.PresentationLanguage ?? CSharpLanguage.Instance;
            var sb = new StringBuilder();

            if (element is IModifiersOwner mod)
            {
                // IClass.IsStatic is documented [Obsolete] in the SDK ("Type are always static
                // members, use IsStaticClass() extension method instead") and unconditionally
                // returns true for EVERY class - confirmed live 2026-07-13: a plain "public class
                // Foo" was reported as "static class Foo" by this tool. The same IModifiersOwner.
                // IsStatic getter is inherited by every other type kind too (struct/interface/
                // enum/delegate), which can never legitimately be "static" in C# at all, so it's
                // equally meaningless there - never trust it for a type. IsAbstract has a related,
                // subtler wrinkle for classes specifically: the C# compiler encodes "static class"
                // as IL abstract+sealed, so IsAbstract is ALSO true for a static class even though
                // the source never wrote "abstract" - confirmed live ("public static class Foo"
                // reported as "static abstract class Foo"). Neither wrinkle affects MEMBERS
                // (methods/fields/properties/etc), where IsStatic/IsAbstract were confirmed
                // correct against the real keyword - only the type-declaration case is special-cased below.
                if (element is ITypeElement)
                {
                    if (element is IClass classElement)
                    {
                        if (CSharpDeclaredElementUtil.IsStaticClass(classElement))
                            sb.Append("static ");
                        else if (mod.IsAbstract)
                            sb.Append("abstract ");
                    }
                    // IStruct/IInterface/IEnum/IDelegate: static/abstract are pure IL-encoding
                    // artifacts of the type kind here, never a real source keyword - print neither.
                }
                else
                {
                    if (mod.IsStatic) sb.Append("static ");
                    if (mod.IsAbstract) sb.Append("abstract ");
                    if (element is IMethod m)
                    {
                        if (m.IsVirtual) sb.Append("virtual ");
                        if (m.IsOverride) sb.Append("override ");
                    }
                }
            }

            sb.Append(element.GetElementType().PresentableName);
            sb.Append(' ');
            sb.Append(element.ShortName);

            if (element is IMethod || element is JetBrains.ReSharper.Psi.CSharp.DeclaredElements.ILocalFunction)
                AppendParams(sb, (IParametersOwner)element, lang);
            else if (element is IParametersOwner po && po.Parameters.Count > 0)
                AppendParams(sb, po, lang);

            if (element is IMethod method)
            {
                sb.Append(" : ");
                sb.Append(method.ReturnType.GetPresentableName(lang));
            }
            else if (element is JetBrains.ReSharper.Psi.CSharp.DeclaredElements.ILocalFunction localFunc)
            {
                sb.Append(" : ");
                sb.Append(localFunc.ReturnType.GetPresentableName(lang));
            }
            else if (element is ITypeOwner typeOwner)
            {
                sb.Append(" : ");
                sb.Append(typeOwner.Type.GetPresentableName(lang));
            }

            return sb.ToString();
        }

        private static void AppendParams(StringBuilder sb, IParametersOwner owner, PsiLanguageType lang)
        {
            sb.Append('(');
            var first = true;
            foreach (var p in owner.Parameters)
            {
                if (!first) sb.Append(", ");
                sb.Append(p.ShortName);
                sb.Append(':');
                sb.Append(p.Type.GetPresentableName(lang));
                first = false;
            }
            sb.Append(')');
        }
    }
}
