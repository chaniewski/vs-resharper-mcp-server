using System.Collections.Generic;
using System.IO;
using System.Text;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.CSharp.Util;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // New in M9 (not from the reference repo - see docs/DEVNOTES.md). Generates XML doc comment stubs
    // (<summary>/<param>/<typeparam>/<returns>/<exception>, following whatever ReSharper's own template
    // would produce) for undocumented symbols. NOT LIVE-TESTED - written during an autonomous
    // unsupervised session with no VS instance available to test against.
    //
    // Uses ReSharper's OWN stub-generation and doc-comment-attachment primitives directly, rather than
    // building XML text by hand and splicing it into the tree as a plain comment token - found by
    // decompiling JetBrains.ReSharper.Feature.Services.CSharp.Generate.CSharpXmlDocumentationInitializer
    // (the class behind ReSharper's own generated-member doc-comment behavior), which does exactly:
    //   var block = CSharpElementFactory.GetInstance(declaration).CreateDocCommentBlock(xmlText);
    //   XmlDocTemplateUtil.FindDocCommentOwner(declaration).SetDocCommentBlock(block);
    // - a real, first-class PSI operation, not a workaround. The XML content itself comes from
    // JetBrains.ReSharper.Psi.CSharp.Util.XmlDocTemplateUtil.GetDocTemplate(IDocCommentBlockOwner, out
    // int cursor) - the SAME template-building utility ReSharper's own "type /// and get an
    // auto-generated stub" editor behavior is built on (confirmed by reading its source: it inspects
    // the declaration's actual parameters/type parameters/return type/thrown exceptions to build
    // <param>/<typeparam>/<returns>/<exception> entries, exactly matching "full stubs, not
    // summary-only"). GetDocTemplate returns multiple sibling top-level XML elements with no single
    // root, so - matching the one other real call site found for multi-element content
    // (CreateCommentsForOperator, also in the decompiled source) - it's wrapped in a single
    // "<member>...</member>" root before being handed to CreateDocCommentBlock.
    //
    // v1 scope: a single named symbol, OR every doc-commentable member in one whole file (walking
    // ITypeMemberDeclaration nodes, via the same XmlDocTemplateUtil.FindDocCommentOwner check
    // ReSharper's own generator code uses to decide whether a declaration can carry a doc comment at
    // all). Whole-file mode defaults to PUBLIC members only (AccessRights.PUBLIC, via
    // IAccessRightsOwner.GetAccessRights() on the member's declared element) and skips anything that
    // already has a DocCommentBlock unless 'overwrite' is set - matching the ordinary meaning of "stub
    // generation for undocumented symbols," not "regenerate every doc comment in the file." No
    // solution/project-wide bulk mode (unlike fix_usings) - not called for in the M9 plan item for this
    // tool, and each file's edits are one single-transaction operation regardless (no per-file dispatch
    // risk the way multi-FILE loops have - this tool never touches DaemonHighlightingCollector/
    // DoHighlighting at any point, same safe category as generate_members/fix_usings).
    public class GenerateXmlDocTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public GenerateXmlDocTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(
            string symbolName = null,
            string kind = null,
            string filePath = null,
            int line = 0,
            int column = 0,
            bool scanWholeFile = false,
            bool overwrite = false)
        {
            return PsiThreadDispatcher.ExecuteWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.generate_xml_doc", () =>
                ExecuteCore(symbolName, kind, filePath, line, column, scanWholeFile, overwrite));
        }

        private string ExecuteCore(string symbolName, string kind, string filePath, int line, int column,
            bool scanWholeFile, bool overwrite)
        {
            if (scanWholeFile)
            {
                if (string.IsNullOrEmpty(filePath))
                    return "scanWholeFile requires 'filePath'.";
                if (!string.IsNullOrEmpty(symbolName) || line != 0 || column != 0)
                    return "scanWholeFile cannot be combined with symbolName/line/column - it targets the whole file.";

                return ExecuteWholeFile(filePath, overwrite);
            }

            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(_solution, symbolName, kind, filePath, line, column);
            if (error != null) return error;

            var elementName = declaredElement.ShortName;
            var declarations = declaredElement.GetDeclarations();
            if (declarations.Count == 0)
                return $"'{elementName}' has no source declaration to document (likely a compiled/library type).";

            var applied = new List<string>();
            var skipped = new List<(string location, string reason)>();

            foreach (var declaration in declarations)
            {
                if (!(declaration is ITypeMemberDeclaration memberDecl))
                {
                    skipped.Add((LocationOf(declaration), "not a doc-commentable declaration"));
                    continue;
                }

                var outcome = TryApply(memberDecl, overwrite);
                if (outcome.applied)
                    applied.Add(LocationOf(declaration));
                else
                    skipped.Add((LocationOf(declaration), outcome.reason));
            }

            return FormatResult(elementName, applied, skipped);
        }

        private string ExecuteWholeFile(string filePath, bool overwrite)
        {
            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return $"File not found in solution: {filePath}";

            var csharpFile = PsiHelpers.GetPsiFile(sourceFile) as ICSharpFile;
            if (csharpFile == null)
                return "generate_xml_doc only supports C# files";

            var applied = new List<string>();
            var skipped = new List<(string location, string reason)>();

            foreach (var node in csharpFile.Descendants())
            {
                if (!(node is ITypeMemberDeclaration memberDecl))
                    continue;

                var declaredElement = memberDecl.DeclaredElement as IAccessRightsOwner;
                if (declaredElement == null || declaredElement.GetAccessRights() != AccessRights.PUBLIC)
                    continue;

                var outcome = TryApply(memberDecl, overwrite);
                if (outcome.applied)
                    applied.Add(LocationOf(memberDecl));
                else if (outcome.reason != "already documented" && outcome.reason != "not doc-commentable")
                    skipped.Add((LocationOf(memberDecl), outcome.reason));
            }

            var sb = new StringBuilder();
            sb.Append(filePath).AppendLine(" - generate_xml_doc results (public members only)");
            sb.AppendLine();
            sb.Append("added ").Append(applied.Count).AppendLine(" doc comment stub(s):");
            foreach (var loc in applied)
                sb.Append("  ").AppendLine(loc);

            if (skipped.Count > 0)
            {
                sb.AppendLine();
                sb.Append(skipped.Count).AppendLine(" skipped:");
                foreach (var (location, reason) in skipped)
                    sb.Append("  ").Append(location).Append(" - ").AppendLine(reason);
            }

            return sb.ToString().TrimEnd();
        }

        private static (bool applied, string reason) TryApply(ITypeMemberDeclaration memberDecl, bool overwrite)
        {
            var owner = XmlDocTemplateUtil.FindDocCommentOwner(memberDecl);
            if (owner == null)
                return (false, "not doc-commentable");

            if (owner.DocCommentBlock != null && !overwrite)
                return (false, "already documented");

            var template = XmlDocTemplateUtil.GetDocTemplate(owner, out _);
            if (string.IsNullOrWhiteSpace(template))
                return (false, "no doc template content for this declaration kind");

            // GetDocTemplate returns sibling top-level elements (<summary>, <param>, ...) with no
            // single root, so wrapping in <member> is required for XMLDocUtil.Load to parse it at all -
            // but CreateDocCommentBlock must NOT receive that wrapper verbatim, or the literal
            // "<member>"/"</member>" tags render as real /// lines in the generated comment (found live,
            // 2026-07-12 - see docs/DEVNOTES.md). The real two-step process, confirmed by decompiling
            // CSharpXmlDocumentationInitializer.CreateCommentsForOperator (which does exactly this, not
            // a direct CreateDocCommentBlock(wrappedText) call as first assumed): parse the wrapped text
            // into a real XmlNode via XMLDocUtil.Load, then re-serialize via
            // XmlDocPresenterUtil.LayoutXml - which specifically special-cases a "member"-named root by
            // recursing into its children WITHOUT writing the "member" tags themselves, correctly
            // stripping the wrapper back out.
            var wrapped = new StringBuilder("<member>\r\n").Append(template).Append("</member>");
            if (!XMLDocUtil.Load(wrapped, out var xmlNode))
                return (false, "generated XML doc template failed to parse (unexpected - please report)");

            var writer = new StringWriter();
            XmlDocPresenterUtil.LayoutXml(xmlNode, writer);

            var factory = CSharpElementFactory.GetInstance(memberDecl);
            var docCommentBlock = factory.CreateDocCommentBlock(writer.ToString());
            owner.SetDocCommentBlock(docCommentBlock);

            return (true, null);
        }

        private static string LocationOf(ITreeNode node)
        {
            var range = TreeNodeExtensions.GetDocumentRange(node);
            if (!range.IsValid())
                return "(unknown location)";

            var (line, col) = PsiHelpers.GetLineColumn(range.StartOffset);
            return $"{line}:{col}";
        }

        private static string FormatResult(string elementName, List<string> applied, List<(string location, string reason)> skipped)
        {
            var sb = new StringBuilder();
            sb.Append("generate_xml_doc for '").Append(elementName).Append('\'').AppendLine();

            if (applied.Count > 0)
            {
                sb.AppendLine();
                sb.Append("added ").Append(applied.Count).AppendLine(" doc comment stub(s):");
                foreach (var loc in applied)
                    sb.Append("  ").AppendLine(loc);
            }

            if (skipped.Count > 0)
            {
                sb.AppendLine();
                sb.Append(skipped.Count).AppendLine(" skipped:");
                foreach (var (location, reason) in skipped)
                    sb.Append("  ").Append(location).Append(" - ").AppendLine(reason);
            }

            if (applied.Count == 0 && skipped.Count == 0)
                sb.AppendLine("\nnothing to document");

            return sb.ToString().TrimEnd();
        }
    }
}
