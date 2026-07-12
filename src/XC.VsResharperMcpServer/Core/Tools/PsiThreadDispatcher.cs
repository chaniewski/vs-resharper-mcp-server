using System;
using System.Threading;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Transactions;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Generic version of resharper-mcp's McpServerComponent.ExecuteOnPsiThread (see docs/DEVNOTES.md) —
    // bridges a synchronous MCP tool call (running on an HTTP-dispatch thread) onto the ReSharper PSI
    // thread via IShellLocks, blocking the caller until the PSI-thread work completes or times out.
    //
    // Every dispatched action also runs inside CompilationContextCookie.GetExplicitUniversalContextIfNotSet()
    // - found live (2026-07-12, see docs/DEVNOTES.md) via a real extract_method crash:
    // ModuleReferenceResolveContextExtensions.GetRuntimeFeatures threw a NullReferenceException deep
    // inside a code-style inspector (MultipleVariableDeclarationCodeStyleInspector, triggered by
    // CodeStyleUtil.ApplyRecursive reformatting the extracted call site) because no
    // IModuleReferenceResolveContext was set on this dispatch thread. When ReSharper runs interactively,
    // the IDE establishes this "compilation context" (which target framework/configuration's runtime
    // features are visible) as ambient state tied to the active document/editor; a headless dispatch
    // like this one never goes through that setup path at all, so anything downstream that assumes a
    // context is already explicit (CompilationContextCookie.IsContextExplicit()) NREs instead of
    // gracefully falling back. GetExplicitUniversalContextIfNotSet() is the SDK's own purpose-built,
    // public fix for exactly this gap - a no-op if a context is already explicit (reference-counted,
    // decompiled and confirmed safe to nest/always apply), so it's applied here centrally for every
    // tool's dispatch rather than patched into extract_method alone - any other tool that happens to
    // walk into code-style/formatting/runtime-feature-checking code deep in the SDK could hit the exact
    // same gap.
    public static class PsiThreadDispatcher
    {
        private const int ToolTimeoutSeconds = 120;

        public static T ExecuteRead<T>(IShellLocks shellLocks, ISolution solution, string operationName, Func<T> action)
        {
            return Execute(shellLocks, solution, operationName, action,
                (locks, op, work) => locks.ExecuteOrQueueReadLock(op, work));
        }

        // For write tools that need the host to open an auto-commit PSI transaction around their
        // mutation (the reference repo's plain IMcpWriteTool path) — e.g. fix_usings, generate_members.
        public static T ExecuteWrite<T>(IShellLocks shellLocks, ISolution solution, string operationName, Func<T> action)
        {
            return Execute(shellLocks, solution, operationName, () =>
            {
                using (PsiTransactionCookie.CreateAutoCommitCookieWithCachesUpdate(solution.GetPsiServices(), operationName))
                {
                    return action();
                }
            }, (locks, op, work) => locks.ExecuteOrQueue(op, () => locks.ExecuteWithWriteLock(work)));
        }

        // For "self-transacting" write tools that manage their own PSI transaction internally (or, in
        // complete_at's case, perform no writes at all and only need main-thread dispatch) — the host
        // must NOT wrap them in an outer transaction. Mirrors the reference's IMcpSelfTransactingWriteTool path.
        public static T ExecuteSelfTransactingWrite<T>(IShellLocks shellLocks, ISolution solution, string operationName, Func<T> action)
        {
            return Execute(shellLocks, solution, operationName, action,
                (locks, op, work) => locks.ExecuteOrQueue(op, () => locks.ExecuteWithWriteLock(work)));
        }

        private static T Execute<T>(
            IShellLocks shellLocks, ISolution solution, string operationName, Func<T> action,
            Action<IShellLocks, string, Action> dispatch)
        {
            var result = default(T);
            Exception caught = null;
            var done = new ManualResetEventSlim(false);
            var cancelled = new CancellationTokenSource();

            dispatch(shellLocks, operationName, () =>
            {
                if (cancelled.IsCancellationRequested)
                    return;

                try
                {
                    solution.GetPsiServices().Files.CommitAllDocuments();
                    using (CompilationContextCookie.GetExplicitUniversalContextIfNotSet())
                    {
                        result = action();
                    }
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
                finally
                {
                    done.Set();
                }
            });

            if (!done.Wait(TimeSpan.FromSeconds(ToolTimeoutSeconds)))
            {
                cancelled.Cancel();
                throw new TimeoutException(
                    $"Timed out after {ToolTimeoutSeconds}s waiting for R# to process '{operationName}'. " +
                    "The IDE may be busy indexing or performing another operation.");
            }

            if (caught != null)
            {
                // Every tool's Execute returns string in practice. Report the real exception as the
                // tool's own text output instead of letting it hit the MCP SDK's tool-invocation
                // wrapper, which swallows it into a generic "An error occurred invoking 'x'" with no
                // detail - exactly the dead end an earlier debugging session ran into (see
                // docs/DEVNOTES.md). For any hypothetical non-string T, preserve the old throw.
                if (typeof(T) == typeof(string))
                    return (T)(object)("Tool '" + operationName + "' failed: " + caught);

                throw caught;
            }

            return result;
        }
    }
}
