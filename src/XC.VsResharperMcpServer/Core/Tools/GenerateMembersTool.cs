using System;
using System.Collections.Generic;
using System.Text;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.ReSharper.Psi.Util;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's GenerateMembersTool (see docs/DEVNOTES.md). Generates members
    // using ONLY low-level PSI tree mutation via CSharpElementFactory + AddClassMemberDeclaration -
    // deliberately NOT the generator-workflow framework, which re-schedules work onto the R# main
    // thread and would deadlock the host given this tool is already dispatched onto that thread
    // under a write lock (see the reference's extensive comment on this exact failure mode).
    //
    // Kinds: 'constructor' and 'equality-members' are fully implemented. 'implement-interface' and
    // 'override-members' are declined - correctly mapping arbitrary member signatures with the raw
    // factory is error-prone and unverified here, so a clear "not supported" beats silently-wrong code.
    public class GenerateMembersTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public GenerateMembersTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(
            string kind,
            string symbolName = null,
            string[] memberNames = null,
            string filePath = null,
            int line = 0,
            int column = 0)
        {
            return PsiThreadDispatcher.ExecuteWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.generate_members", () =>
                ExecuteCore(kind, symbolName, memberNames, filePath, line, column));
        }

        private string ExecuteCore(string kindArg, string symbolName, string[] memberNames, string filePath, int line, int column)
        {
            if (string.IsNullOrEmpty(kindArg))
                return "Missing required argument 'kind'. Use one of: 'constructor', 'equality-members'.";

            var normalizedKind = kindArg.ToLowerInvariant();
            if (normalizedKind != "implement-interface" &&
                normalizedKind != "override-members" &&
                normalizedKind != "constructor" &&
                normalizedKind != "equality-members")
                return $"Unknown kind '{kindArg}'. Use one of: 'constructor', 'equality-members'.";

            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(
                _solution,
                symbolName,
                string.IsNullOrEmpty(symbolName) ? null : "type",
                filePath, line, column);

            if (error != null) return error;

            if (!(declaredElement is ITypeElement typeElement))
                return $"Target '{declaredElement.ShortName}' is not a type. generate_members requires a class/struct/record type.";

            var targetName = PsiHelpers.GetQualifiedName(typeElement);
            var filter = memberNames != null && memberNames.Length > 0 ? new HashSet<string>(memberNames) : null;

            try
            {
                switch (normalizedKind)
                {
                    case "constructor":
                        return GenerateConstructor(typeElement, targetName, kindArg, filter);
                    case "equality-members":
                        return GenerateEqualityMembers(typeElement, targetName, kindArg, filter);
                    default:
                        return $"'{kindArg}' is not yet supported. generate_members currently supports 'constructor' and " +
                               "'equality-members' (implemented with low-level PSI). Signature mapping for interface/override " +
                               "members is not implemented to avoid generating incorrect code.";
                }
            }
            catch (Exception ex)
            {
                return $"generate_members failed: {ex.Message}";
            }
        }

        private static (IClassLikeDeclaration decl, string error) ResolveClassDeclaration(
            ITypeElement typeElement, string targetName, string kindArg)
        {
            var declarations = typeElement.GetDeclarations();
            if (declarations.Count == 0)
                return (null, $"Type '{typeElement.ShortName}' has no source declaration to generate into (likely a compiled/library type).");

            foreach (var d in declarations)
            {
                if (d.GetSourceFile() == null) continue;
                if (d is IClassLikeDeclaration classLike)
                    return (classLike, null);
            }

            return (null, $"Type '{typeElement.ShortName}' has no editable class/struct/record declaration to generate into.");
        }

        private struct DataMember
        {
            public string Name;
            public IType Type;
        }

        private static List<DataMember> CollectDataMembers(ITypeElement typeElement, HashSet<string> filter)
        {
            var result = new List<DataMember>();
            var seen = new HashSet<string>();

            foreach (var field in typeElement.Fields)
            {
                if (field.IsStatic || field.IsConstant) continue;
                var name = field.ShortName;
                if (string.IsNullOrEmpty(name)) continue;
                if (name.IndexOf('<') >= 0 || name.IndexOf('$') >= 0) continue;
                if (filter != null && !filter.Contains(name)) continue;
                if (!seen.Add(name)) continue;
                result.Add(new DataMember { Name = name, Type = field.Type });
            }

            foreach (var property in typeElement.Properties)
            {
                if (property.IsStatic) continue;
                if (!property.IsWritable) continue;
                if (property.Parameters.Count > 0) continue;
                var name = property.ShortName;
                if (string.IsNullOrEmpty(name)) continue;
                if (filter != null && !filter.Contains(name)) continue;
                if (!seen.Add(name)) continue;
                result.Add(new DataMember { Name = name, Type = property.Type });
            }

            return result;
        }

        private static List<DataMember> CollectReadableMembers(ITypeElement typeElement, HashSet<string> filter)
        {
            var result = new List<DataMember>();
            var seen = new HashSet<string>();

            foreach (var field in typeElement.Fields)
            {
                if (field.IsStatic || field.IsConstant) continue;
                var name = field.ShortName;
                if (string.IsNullOrEmpty(name)) continue;
                if (name.IndexOf('<') >= 0 || name.IndexOf('$') >= 0) continue;
                if (filter != null && !filter.Contains(name)) continue;
                if (!seen.Add(name)) continue;
                result.Add(new DataMember { Name = name, Type = field.Type });
            }

            foreach (var property in typeElement.Properties)
            {
                if (property.IsStatic) continue;
                if (!property.IsReadable) continue;
                if (property.Parameters.Count > 0) continue;
                var name = property.ShortName;
                if (string.IsNullOrEmpty(name)) continue;
                if (filter != null && !filter.Contains(name)) continue;
                if (!seen.Add(name)) continue;
                result.Add(new DataMember { Name = name, Type = property.Type });
            }

            return result;
        }

        private string GenerateConstructor(ITypeElement typeElement, string targetName, string kindArg, HashSet<string> filter)
        {
            var (classDecl, declError) = ResolveClassDeclaration(typeElement, targetName, kindArg);
            if (declError != null) return declError;

            var members = CollectDataMembers(typeElement, filter);
            if (members.Count == 0)
                return filter != null
                    ? $"None of the requested member(s) are assignable fields/properties on '{typeElement.ShortName}'."
                    : $"'{typeElement.ShortName}' has no instance fields or settable properties to initialize in a constructor.";

            var factory = CSharpElementFactory.GetInstance(classDecl);

            var args = new List<object>();
            var parms = new List<string>();
            var body = new StringBuilder();
            for (var i = 0; i < members.Count; i++)
            {
                var paramName = ToParameterName(members[i].Name, i);
                parms.Add("$" + i + " " + paramName);
                body.Append("this.").Append(members[i].Name).Append(" = ").Append(paramName).Append(";\n");
                args.Add(members[i].Type);
            }

            var ctorName = typeElement.ShortName;
            var text = "public " + ctorName + "(" + string.Join(", ", parms) + ") {\n" + body + "}";

            var member = factory.CreateTypeMemberDeclaration(text, args.ToArray());
            var inserted = classDecl.AddClassMemberDeclaration((IClassMemberDeclaration)member);

            return ShapeResult(targetName, kindArg, classDecl, new[] { (IClassMemberDeclaration)inserted });
        }

        private string GenerateEqualityMembers(ITypeElement typeElement, string targetName, string kindArg, HashSet<string> filter)
        {
            var (classDecl, declError) = ResolveClassDeclaration(typeElement, targetName, kindArg);
            if (declError != null) return declError;

            var members = CollectDataMembers(typeElement, filter);
            if (members.Count == 0)
                members = CollectReadableMembers(typeElement, filter);

            var factory = CSharpElementFactory.GetInstance(classDecl);
            var selfType = TypeFactory.CreateType(typeElement);
            var inserted = new List<IClassMemberDeclaration>();

            {
                var sb = new StringBuilder();
                sb.Append("public bool Equals($0 other) {\n");
                if (members.Count == 0)
                {
                    sb.Append("return !ReferenceEquals(null, other);\n");
                }
                else
                {
                    sb.Append("if (ReferenceEquals(null, other)) return false;\n");
                    sb.Append("if (ReferenceEquals(this, other)) return true;\n");
                    sb.Append("return ");
                    for (var i = 0; i < members.Count; i++)
                    {
                        if (i > 0) sb.Append(" && ");
                        var n = members[i].Name;
                        sb.Append("System.Collections.Generic.EqualityComparer<$")
                          .Append(i + 1)
                          .Append(">.Default.Equals(this.").Append(n).Append(", other.").Append(n).Append(")");
                    }
                    sb.Append(";\n");
                }
                sb.Append("}");

                var args = new List<object> { selfType };
                foreach (var m in members) args.Add(m.Type);

                var member = factory.CreateTypeMemberDeclaration(sb.ToString(), args.ToArray());
                inserted.Add((IClassMemberDeclaration)classDecl.AddClassMemberDeclaration((IClassMemberDeclaration)member));
            }

            {
                var member = factory.CreateTypeMemberDeclaration(
                    "public override bool Equals(object obj) {\n" +
                    "if (ReferenceEquals(null, obj)) return false;\n" +
                    "if (ReferenceEquals(this, obj)) return true;\n" +
                    "return obj is $0 other && Equals(other);\n" +
                    "}",
                    selfType);
                inserted.Add((IClassMemberDeclaration)classDecl.AddClassMemberDeclaration((IClassMemberDeclaration)member));
            }

            {
                var sb = new StringBuilder();
                sb.Append("public override int GetHashCode() {\n");
                if (members.Count == 0)
                {
                    sb.Append("return 0;\n");
                }
                else
                {
                    sb.Append("unchecked {\n");
                    sb.Append("int hashCode = 17;\n");
                    foreach (var m in members)
                    {
                        // Non-nullable value types can never equal null - a "!= null ? x : 0" guard
                        // there is dead code and MSBuild flags it (CS0472 "always true"). Only guard
                        // reference types and Nullable<T> members, where the check is meaningful.
                        if (m.Type.IsValueType() && !m.Type.IsNullable())
                        {
                            sb.Append("hashCode = (hashCode * 397) ^ this.").Append(m.Name).Append(".GetHashCode();\n");
                        }
                        else
                        {
                            sb.Append("hashCode = (hashCode * 397) ^ (this.").Append(m.Name)
                              .Append(" != null ? this.").Append(m.Name).Append(".GetHashCode() : 0);\n");
                        }
                    }
                    sb.Append("return hashCode;\n");
                    sb.Append("}\n");
                }
                sb.Append("}");

                var member = factory.CreateTypeMemberDeclaration(sb.ToString());
                inserted.Add((IClassMemberDeclaration)classDecl.AddClassMemberDeclaration((IClassMemberDeclaration)member));
            }

            return ShapeResult(targetName, kindArg, classDecl, inserted);
        }

        private string ShapeResult(string targetName, string kindArg, IClassLikeDeclaration classDecl, IReadOnlyList<IClassMemberDeclaration> inserted)
        {
            var sourceFile = classDecl.GetSourceFile();
            var filePath = sourceFile?.GetLocation().FullPath;

            var sb = new StringBuilder();
            sb.Append(targetName).Append(" - generated ").Append(inserted.Count).Append(" member(s) (").Append(kindArg).Append(')');
            if (filePath != null) sb.Append(" in ").Append(filePath);
            sb.AppendLine();

            foreach (var memberDecl in inserted)
            {
                if (memberDecl == null) continue;

                var declaredEl = memberDecl.DeclaredElement;
                sb.AppendLine();
                sb.Append(declaredEl?.GetElementType()?.PresentableName ?? kindArg).Append(' ').Append(declaredEl?.ShortName ?? "(member)");

                var range = TreeNodeExtensions.GetDocumentRange(memberDecl);
                if (range.IsValid())
                {
                    var (line, col) = PsiHelpers.GetLineColumn(range.StartOffset);
                    sb.Append(" - :").Append(line).Append(':').Append(col);
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static string ToParameterName(string memberName, int index)
        {
            if (string.IsNullOrEmpty(memberName)) return "p" + index;

            var name = memberName;
            if (name.StartsWith("m_") && name.Length > 2) name = name.Substring(2);
            name = name.TrimStart('_');
            if (name.Length == 0) return "p" + index;

            var first = char.ToLowerInvariant(name[0]);
            var candidate = first + name.Substring(1);

            if (IsCSharpKeyword(candidate)) candidate = "@" + candidate;
            return candidate;
        }

        private static bool IsCSharpKeyword(string s)
        {
            switch (s)
            {
                case "value": case "object": case "string": case "int": case "bool":
                case "byte": case "char": case "double": case "float": case "long":
                case "short": case "decimal": case "void": case "class": case "struct":
                case "this": case "base": case "null": case "true": case "false":
                case "new": case "return": case "params": case "ref": case "out":
                case "in": case "event": case "delegate": case "namespace": case "using":
                    return true;
                default:
                    return false;
            }
        }
    }
}
