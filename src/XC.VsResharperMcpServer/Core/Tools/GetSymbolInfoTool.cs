using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.Tree;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's GetSymbolInfoTool (see docs/DEVNOTES.md). Batch mode dropped for M3.
    public class GetSymbolInfoTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public GetSymbolInfoTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(
            string symbolName = null,
            string kind = null,
            bool includeMembers = false,
            string filePath = null,
            int line = 0,
            int column = 0)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.get_symbol_info", () =>
                ExecuteCore(symbolName, kind, includeMembers, filePath, line, column));
        }

        private string ExecuteCore(string symbolName, string kind, bool includeMembers, string filePath, int line, int column)
        {
            var (declaredElement, resolveError) = PsiHelpers.ResolveFromArgs(_solution, symbolName, kind, filePath, line, column);
            if (resolveError != null) return resolveError;

            var lang = declaredElement.PresentationLanguage ?? CSharpLanguage.Instance;
            var sb = new StringBuilder();

            sb.AppendLine(PsiHelpers.FormatSignature(declaredElement));

            if (declaredElement is IClrDeclaredElement clrElement)
            {
                var containingType = clrElement.GetContainingType();
                if (containingType != null)
                    sb.Append("containingType: ").AppendLine(containingType.GetClrName().FullName);

                if (declaredElement is ITypeElement typeElem)
                {
                    var ns = typeElem.GetContainingNamespace();
                    if (ns != null && !string.IsNullOrEmpty(ns.QualifiedName))
                        sb.Append("namespace: ").AppendLine(ns.QualifiedName);
                }
                else if (containingType != null)
                {
                    var ns = containingType.GetContainingNamespace();
                    if (ns != null && !string.IsNullOrEmpty(ns.QualifiedName))
                        sb.Append("namespace: ").AppendLine(ns.QualifiedName);
                }
            }

            if (declaredElement is ITypeElement typeElement)
            {
                var superTypes = typeElement.GetSuperTypes()
                    .Select(t => t.GetPresentableName(lang))
                    .ToList();
                if (superTypes.Any())
                    sb.Append("baseTypes: ").AppendLine(string.Join(", ", superTypes));
            }

            var declarations = declaredElement.GetDeclarations();
            if (declarations.Count > 0)
            {
                var decl = declarations[0];
                var range = TreeNodeExtensions.GetDocumentRange(decl);
                if (range.IsValid())
                {
                    var (declLine, declCol) = PsiHelpers.GetLineColumn(range.StartOffset);
                    var file = decl.GetSourceFile()?.GetLocation().FullPath;
                    if (file != null)
                        sb.Append("declared: ").Append(file).Append(':').Append(declLine).Append(':').AppendLine(declCol.ToString());
                }
            }

            AppendXmlDocumentation(sb, declaredElement);

            if (includeMembers && declaredElement is ITypeElement membersType)
            {
                var propertyNames = new HashSet<string>();
                var eventNames = new HashSet<string>();
                foreach (var m in membersType.GetMembers())
                {
                    if (m is IProperty prop) propertyNames.Add(prop.ShortName);
                    if (m is IEvent evt) eventNames.Add(evt.ShortName);
                }

                sb.AppendLine();
                sb.AppendLine("members:");

                foreach (var member in membersType.GetMembers())
                {
                    if (member is IMethod accessorMethod)
                    {
                        var name = accessorMethod.ShortName;
                        if ((name.StartsWith("get_") || name.StartsWith("set_")) &&
                            propertyNames.Contains(name.Substring(4)))
                            continue;
                        if ((name.StartsWith("add_") || name.StartsWith("remove_")) &&
                            eventNames.Contains(name.Substring(name.IndexOf('_') + 1)))
                            continue;
                    }

                    if (IsCompilerGeneratedMember(member))
                        continue;

                    sb.Append("  ").AppendLine(PsiHelpers.FormatSignature(member));
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static void AppendXmlDocumentation(StringBuilder sb, IDeclaredElement declaredElement)
        {
            var xmlDoc = TryGetXmlDoc(declaredElement);
            var summary = xmlDoc?.SelectSingleNode("//summary")?.InnerText?.Trim();
            var remarks = xmlDoc?.SelectSingleNode("//remarks")?.InnerText?.Trim();

            if (string.IsNullOrEmpty(summary) && string.IsNullOrEmpty(remarks))
            {
                var fallback = TryReadXmlDocumentationFromSource(declaredElement);
                summary = fallback.summary;
                remarks = fallback.remarks;
            }

            if (!string.IsNullOrEmpty(summary))
                sb.Append("doc: ").AppendLine(summary);

            if (!string.IsNullOrEmpty(remarks))
                sb.Append("remarks: ").AppendLine(remarks);
        }

        private static XmlNode TryGetXmlDoc(IDeclaredElement declaredElement)
        {
            try
            {
                return declaredElement.GetXMLDoc(true);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static (string summary, string remarks) TryReadXmlDocumentationFromSource(IDeclaredElement declaredElement)
        {
            var declaration = declaredElement.GetDeclarations().FirstOrDefault();
            if (declaration == null)
                return default;

            var sourceFile = declaration.GetSourceFile();
            var filePath = sourceFile?.GetLocation().FullPath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return default;

            var range = TreeNodeExtensions.GetDocumentRange(declaration);
            if (!range.IsValid())
                return default;

            var (declLine, _) = PsiHelpers.GetLineColumn(range.StartOffset);
            if (declLine <= 1)
                return default;

            var lines = File.ReadAllLines(filePath);
            if (declLine - 1 > lines.Length)
                return default;

            var docLines = new List<string>();
            for (var lineIndex = declLine - 2; lineIndex >= 0; lineIndex--)
            {
                var trimmed = lines[lineIndex].TrimStart();
                if (trimmed.StartsWith("///"))
                {
                    docLines.Insert(0, trimmed.Substring(3));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                break;
            }

            if (docLines.Count == 0)
                return default;

            try
            {
                var document = XDocument.Parse("<root>" + string.Join(Environment.NewLine, docLines) + "</root>");
                var root = document.Root;
                if (root == null)
                    return default;

                return (
                    root.Element("summary")?.Value?.Trim(),
                    root.Element("remarks")?.Value?.Trim());
            }
            catch (Exception)
            {
                return default;
            }
        }

        private static bool IsCompilerGeneratedMember(ITypeMember member)
        {
            var name = member.ShortName;

            if (name.StartsWith("$") || name.StartsWith("<"))
                return member.GetDeclarations().Count == 0;

            if (name == "op_Equality" || name == "op_Inequality")
                return true;

            if (name == ".ctor" && member is IParametersOwner ctor && ctor.Parameters.Count == 0)
                return true;

            if (member is IMethod)
            {
                switch (name)
                {
                    case "Equals":
                    case "GetHashCode":
                    case "ToString":
                    case "PrintMembers":
                    case "Deconstruct":
                        return member.GetDeclarations().Count == 0;
                }
            }

            if (member is IProperty && name == "EqualityContract")
                return member.GetDeclarations().Count == 0;

            return false;
        }
    }
}
