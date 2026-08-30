namespace CTilde.Tests;

internal static partial class ConformanceTests
{
    public static void RegisterPart5(ConformanceSuite suite)
    {
        suite.Run("feature C symbol snapshot", () =>
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples", "Features", "Program.ct"));
            var compilation = Compile(source);
            using var writer = new StringWriter();
            Assert(compilation.EmitSymbolMap(writer).Success, "Feature symbol-map emission failed.");
            using var document = System.Text.Json.JsonDocument.Parse(writer.ToString());
            var projection = string.Join('\n', document.RootElement.GetProperty("symbols").EnumerateArray()
                .Where(symbol => symbol.GetProperty("identity").GetString()!.Contains("Examples.", StringComparison.Ordinal))
                .Select(symbol => $"{symbol.GetProperty("kind").GetString()} {symbol.GetProperty("name").GetString()} {symbol.GetProperty("identity").GetString()} -> {symbol.GetProperty("signature").GetString()}")) + "\n";
            var expected = Normalize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Snapshots", "features.symbols.txt")));
            Assert(projection == expected, $"Generated C symbol snapshot changed.{Environment.NewLine}{projection}");
        });

        suite.Run("object ABI snapshot", () =>
        {
            var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Examples", "ObjectModel", "Program.ct"));
            var projection = NormalizeObjectAbi(ProjectObjectAbi(Emit(source)));
            var expected = NormalizeObjectAbi(Normalize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Snapshots", "object-model.abi.txt"))));
            Assert(projection == expected, $"Generated object ABI snapshot changed.{Environment.NewLine}{projection}");
        });

        suite.Run("bounds failure", () =>
        {
            const string source = """
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        int[] values = new int[1];
                        values[1] = 4;
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode != 0, "Bounds failure returned success.");
            Assert(result.StandardError.Contains("CTA0003", StringComparison.Ordinal), result.StandardError);
        });

        suite.Run("null failure", () =>
        {
            const string source = """
                public sealed class Box { public int Value; }
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        Box box = null;
                        int value = box.Value;
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode != 0, "Null failure returned success.");
            Assert(result.StandardError.Contains("CTN0001", StringComparison.Ordinal), result.StandardError);
        });

        suite.Run("negative array length failure", () =>
        {
            const string source = """
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        int length = -1;
                        int[] values = new int[length];
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode != 0, "Negative array length returned success.");
            Assert(result.StandardError.Contains("CTA0001", StringComparison.Ordinal), result.StandardError);
        });

        suite.Run("integer division failure", () =>
        {
            const string source = """
                public static class Program
                {
                    [EntryPoint]
                    public static void Main()
                    {
                        int zero = 0;
                        int value = 4 / zero;
                    }
                }
                """;
            var result = CompileAndRun(source);
            Assert(result.ExitCode != 0, "Division by zero returned success.");
            Assert(result.StandardError.Contains("CTI0001", StringComparison.Ordinal), result.StandardError);
        });

        suite.Run("allocation overflow guard emitted", () =>
        {
            var generated = Emit("public static class Program { [EntryPoint] public static void Main() { int[] values = new int[0]; } }");
            Assert(generated.Contains("CTA0002", StringComparison.Ordinal), "Allocation overflow guard was not emitted.");
        });

        suite.Run("draft 0.10 attached native threads", () =>
        {
            const string source = """
                using System;
                public delegate int Transformer(int value);
                public sealed class Box { public int Value; }
                public static class Native
                {
                    [Extern("ct_test_thread_delegate")] public static int InvokeDelegate([SynchronousCallback] Transformer callback, int value);
                    [Extern("ct_test_thread_pointer")] public static unsafe int InvokePointer(delegate* unmanaged<int, int> callback, int value);
                    [Extern("ct_test_thread_export")] public static int InvokeExport(int left, int right);
                    [Extern("ct_test_arc_threads")] public static void StressArc([Retained] object value);
                    [Extern("ct_test_thread_exceptions")] public static int TestExceptions();
                    [Extern("ct_test_thread_identities")] public static int TestIdentities();
                    [Extern("ct_test_start_deferred_release")] public static void StartDeferredRelease([Retained] object value);
                    [Extern("ct_test_finish_deferred_release")] public static int FinishDeferredRelease();
                }
                public static class Program
                {
                    private static int AddOne(int value) { return value + 1; }
                    private static void Observe(int value) { }
                    [Export("ctilde_thread_add")] public static int Add(int left, int right) { return left + right; }
                    [Export("ctilde_thread_probe")]
                    public static int Probe(int value)
                    {
                        defer Observe(value);
                        try
                        {
                            if (value < 0)
                                throw new Exception("thread probe");
                        }
                        catch (Exception)
                        {
                            value = -value;
                        }
                        return value + 1;
                    }
                    [Export("ctilde_thread_allocate_hash")]
                    public static int AllocateHash(int value)
                    {
                        Box box = new Box();
                        box.Value = value;
                        return box.GetHashCode();
                    }
                    [EntryPoint] public static unsafe void Main()
                    {
                        Box shared = new Box();
                        shared.Value = 7;
                        Native.StressArc(shared);
                        Transformer callback = AddOne;
                        Console.WriteLine(Native.InvokeDelegate(callback, 41));
                        delegate* unmanaged<int, int> pointer = &AddOne;
                        Console.WriteLine(Native.InvokePointer(pointer, 41));
                        Console.WriteLine(Native.InvokeExport(20, 22));
                        Console.WriteLine(Native.TestExceptions());
                        Console.WriteLine(Native.TestIdentities());
                        {
                            Box finalOnWorker = new Box();
                            Native.StartDeferredRelease(finalOnWorker);
                        }
                        Console.WriteLine(Native.FinishDeferredRelease());
                    }
                }
                """;
            const string native = """

                #if defined(_WIN32)
                #include <windows.h>
                #else
                #include <pthread.h>
                #endif

                typedef int32_t (*ct_test_delegate_fn)(int32_t, void*);
                typedef int32_t (*ct_test_pointer_fn)(int32_t);
                typedef struct ct_test_thread_context {
                    int mode;
                    int32_t left;
                    int32_t right;
                    int32_t result;
                    ct_test_delegate_fn delegate_callback;
                    ct_test_pointer_fn pointer_callback;
                    void* callback_context;
                    ct_managed_object* object;
                } ct_test_thread_context;

                #if defined(_WIN32)
                static DWORD WINAPI ct_test_thread_worker(LPVOID raw)
                #else
                static void* ct_test_thread_worker(void* raw)
                #endif
                {
                    ct_test_thread_context* context = (ct_test_thread_context*)raw;
                    ct_thread_attach();
                    if (context->mode == 0)
                        context->result = context->delegate_callback(context->left, context->callback_context);
                    else if (context->mode == 1)
                        context->result = context->pointer_callback(context->left);
                    else if (context->mode == 2)
                        context->result = ctilde_thread_add(context->left, context->right);
                    else if (context->mode == 3)
                    {
                        for (int index = 0; index < 20000; index++)
                        {
                            ct_retain((ct_object*)(void*)context->object);
                            ct_release((ct_object*)(void*)context->object);
                        }
                        ct_release((ct_object*)(void*)context->object);
                    }
                    else if (context->mode == 4)
                        context->result = ctilde_thread_probe(context->left);
                    else
                        context->result = ctilde_thread_allocate_hash(context->left);
                    ct_thread_detach();
                    #if defined(_WIN32)
                    return 0;
                    #else
                    return NULL;
                    #endif
                }

                static void ct_test_run_thread(ct_test_thread_context* context)
                {
                    #if defined(_WIN32)
                    HANDLE thread = CreateThread(NULL, 0, ct_test_thread_worker, context, 0, NULL);
                    if (thread == NULL) abort();
                    (void)WaitForSingleObject(thread, INFINITE);
                    (void)CloseHandle(thread);
                    #else
                    pthread_t thread;
                    if (pthread_create(&thread, NULL, ct_test_thread_worker, context) != 0) abort();
                    if (pthread_join(thread, NULL) != 0) abort();
                    #endif
                }

                int32_t ct_test_thread_delegate(ct_test_delegate_fn callback, void* callback_context, int32_t value)
                {
                    ct_test_thread_context context = { 0, value, 0, 0, callback, NULL, callback_context, NULL };
                    ct_test_run_thread(&context);
                    return context.result;
                }

                int32_t ct_test_thread_pointer(ct_test_pointer_fn callback, int32_t value)
                {
                    ct_test_thread_context context = { 1, value, 0, 0, NULL, callback, NULL, NULL };
                    ct_test_run_thread(&context);
                    return context.result;
                }

                int32_t ct_test_thread_export(int32_t left, int32_t right)
                {
                    ct_test_thread_context context = { 2, left, right, 0, NULL, NULL, NULL, NULL };
                    ct_test_run_thread(&context);
                    return context.result;
                }

                void ct_test_arc_threads(ct_managed_object* object)
                {
                    ct_test_thread_context contexts[4] = { 0 };
                    #if defined(_WIN32)
                    HANDLE threads[4] = { 0 };
                    #else
                    pthread_t threads[4];
                    #endif
                    for (int index = 0; index < 4; index++)
                    {
                        contexts[index].mode = 3;
                        contexts[index].object = object;
                        ct_retain((ct_object*)(void*)object);
                        #if defined(_WIN32)
                        threads[index] = CreateThread(NULL, 0, ct_test_thread_worker, &contexts[index], 0, NULL);
                        if (threads[index] == NULL) abort();
                        #else
                        if (pthread_create(&threads[index], NULL, ct_test_thread_worker, &contexts[index]) != 0) abort();
                        #endif
                    }
                    for (int index = 0; index < 4; index++)
                    {
                        #if defined(_WIN32)
                        (void)WaitForSingleObject(threads[index], INFINITE);
                        (void)CloseHandle(threads[index]);
                        #else
                        if (pthread_join(threads[index], NULL) != 0) abort();
                        #endif
                    }
                    ct_release((ct_object*)(void*)object);
                }

                int32_t ct_test_thread_exceptions(void)
                {
                    ct_test_thread_context contexts[2] = {
                        { 4, -40, 0, 0, NULL, NULL, NULL, NULL },
                        { 4, 41, 0, 0, NULL, NULL, NULL, NULL },
                    };
                    #if defined(_WIN32)
                    HANDLE threads[2] = { 0 };
                    #else
                    pthread_t threads[2];
                    #endif
                    for (int index = 0; index < 2; index++)
                    {
                        #if defined(_WIN32)
                        threads[index] = CreateThread(NULL, 0, ct_test_thread_worker, &contexts[index], 0, NULL);
                        if (threads[index] == NULL) abort();
                        #else
                        if (pthread_create(&threads[index], NULL, ct_test_thread_worker, &contexts[index]) != 0) abort();
                        #endif
                    }
                    for (int index = 0; index < 2; index++)
                    {
                        #if defined(_WIN32)
                        (void)WaitForSingleObject(threads[index], INFINITE);
                        (void)CloseHandle(threads[index]);
                        #else
                        if (pthread_join(threads[index], NULL) != 0) abort();
                        #endif
                    }
                    return contexts[0].result + contexts[1].result;
                }

                int32_t ct_test_thread_identities(void)
                {
                    ct_test_thread_context contexts[16] = { 0 };
                    #if defined(_WIN32)
                    HANDLE threads[16] = { 0 };
                    #else
                    pthread_t threads[16];
                    #endif
                    for (int index = 0; index < 16; index++)
                    {
                        contexts[index].mode = 5;
                        contexts[index].left = index;
                        #if defined(_WIN32)
                        threads[index] = CreateThread(NULL, 0, ct_test_thread_worker, &contexts[index], 0, NULL);
                        if (threads[index] == NULL) abort();
                        #else
                        if (pthread_create(&threads[index], NULL, ct_test_thread_worker, &contexts[index]) != 0) abort();
                        #endif
                    }
                    for (int index = 0; index < 16; index++)
                    {
                        #if defined(_WIN32)
                        (void)WaitForSingleObject(threads[index], INFINITE);
                        (void)CloseHandle(threads[index]);
                        #else
                        if (pthread_join(threads[index], NULL) != 0) abort();
                        #endif
                        if (contexts[index].result == 0) return 0;
                        for (int previous = 0; previous < index; previous++)
                            if (contexts[index].result == contexts[previous].result) return 0;
                    }
                    return 1;
                }

                static ct_managed_object* ct_test_deferred_object = NULL;
                static uint32_t ct_test_deferred_baseline = 0;
                static int32_t ct_test_deferred_result = 0;
                #if defined(_WIN32)
                static HANDLE ct_test_deferred_thread = NULL;
                static HANDLE ct_test_deferred_event = NULL;
                static DWORD WINAPI ct_test_deferred_worker(LPVOID raw)
                {
                    (void)raw;
                    ct_thread_attach();
                    (void)WaitForSingleObject(ct_test_deferred_event, INFINITE);
                    ct_release((ct_object*)(void*)ct_test_deferred_object);
                    ct_test_deferred_object = NULL;
                    ct_test_deferred_result = ct_memory_diagnostic_live_objects() == ct_test_deferred_baseline;
                    ct_thread_detach();
                    return 0;
                }
                #else
                static pthread_t ct_test_deferred_thread;
                static pthread_mutex_t ct_test_deferred_mutex = PTHREAD_MUTEX_INITIALIZER;
                static pthread_cond_t ct_test_deferred_condition = PTHREAD_COND_INITIALIZER;
                static bool ct_test_deferred_ready = false;
                static void* ct_test_deferred_worker(void* raw)
                {
                    (void)raw;
                    ct_thread_attach();
                    if (pthread_mutex_lock(&ct_test_deferred_mutex) != 0) abort();
                    while (!ct_test_deferred_ready)
                        if (pthread_cond_wait(&ct_test_deferred_condition, &ct_test_deferred_mutex) != 0) abort();
                    if (pthread_mutex_unlock(&ct_test_deferred_mutex) != 0) abort();
                    ct_release((ct_object*)(void*)ct_test_deferred_object);
                    ct_test_deferred_object = NULL;
                    ct_test_deferred_result = ct_memory_diagnostic_live_objects() == ct_test_deferred_baseline;
                    ct_thread_detach();
                    return NULL;
                }
                #endif

                void ct_test_start_deferred_release(ct_managed_object* object)
                {
                    ct_test_deferred_object = object;
                    ct_test_deferred_baseline = ct_memory_diagnostic_live_objects() - 1u;
                    ct_test_deferred_result = 0;
                    #if defined(_WIN32)
                    ct_test_deferred_event = CreateEventA(NULL, TRUE, FALSE, NULL);
                    if (ct_test_deferred_event == NULL) abort();
                    ct_test_deferred_thread = CreateThread(NULL, 0, ct_test_deferred_worker, NULL, 0, NULL);
                    if (ct_test_deferred_thread == NULL) abort();
                    #else
                    ct_test_deferred_ready = false;
                    if (pthread_create(&ct_test_deferred_thread, NULL, ct_test_deferred_worker, NULL) != 0) abort();
                    #endif
                }

                int32_t ct_test_finish_deferred_release(void)
                {
                    #if defined(_WIN32)
                    if (!SetEvent(ct_test_deferred_event)) abort();
                    (void)WaitForSingleObject(ct_test_deferred_thread, INFINITE);
                    (void)CloseHandle(ct_test_deferred_thread);
                    (void)CloseHandle(ct_test_deferred_event);
                    #else
                    if (pthread_mutex_lock(&ct_test_deferred_mutex) != 0) abort();
                    ct_test_deferred_ready = true;
                    if (pthread_cond_signal(&ct_test_deferred_condition) != 0) abort();
                    if (pthread_mutex_unlock(&ct_test_deferred_mutex) != 0) abort();
                    if (pthread_join(ct_test_deferred_thread, NULL) != 0) abort();
                    #endif
                    return ct_test_deferred_result;
                }
                """;
            var result = CompileAndRun(source, memoryDiagnostics: true, nativeSuffix: native, threads: true);
            Assert(result.ExitCode == 0, result.StandardError);
            Assert(Normalize(result.StandardOutput) == "42\n42\n42\n83\n1\n1\n", result.StandardOutput);
        });

        suite.Run("draft 0.10 unattached native entry", () =>
        {
            const string source = """
                public static class Native { [Extern("ct_test_unattached_export")] public static void Invoke(); }
                public static class Program
                {
                    [Export("ctilde_unattached_add")] public static int Add(int left, int right) { return left + right; }
                    [EntryPoint] public static void Main() { Native.Invoke(); }
                }
                """;
            const string native = """

                #if defined(_WIN32)
                #include <windows.h>
                static DWORD WINAPI ct_test_unattached_worker(LPVOID raw) { (void)raw; (void)ctilde_unattached_add(1, 2); return 0; }
                void ct_test_unattached_export(void) { HANDLE thread = CreateThread(NULL, 0, ct_test_unattached_worker, NULL, 0, NULL); if (thread == NULL) abort(); (void)WaitForSingleObject(thread, INFINITE); (void)CloseHandle(thread); }
                #else
                #include <pthread.h>
                static void* ct_test_unattached_worker(void* raw) { (void)raw; (void)ctilde_unattached_add(1, 2); return NULL; }
                void ct_test_unattached_export(void) { pthread_t thread; if (pthread_create(&thread, NULL, ct_test_unattached_worker, NULL) != 0) abort(); if (pthread_join(thread, NULL) != 0) abort(); }
                #endif
                """;
            var result = CompileAndRun(source, nativeSuffix: native, threads: true);
            Assert(result.ExitCode != 0 && result.StandardError.Contains("CTT0001", StringComparison.Ordinal), result.StandardError);
        });

        suite.Run("draft 0.10 invalid attachment lifecycle", () =>
        {
            const string source = """
                public static class Native { [Extern("ct_test_double_attach")] public static void DoubleAttach(); }
                public static class Program { [EntryPoint] public static void Main() { Native.DoubleAttach(); } }
                """;
            const string native = """

                void ct_test_double_attach(void) { ct_thread_attach(); }
                """;
            var result = CompileAndRun(source, nativeSuffix: native);
            Assert(result.ExitCode != 0 && result.StandardError.Contains("CTT0002", StringComparison.Ordinal), result.StandardError);
        });
    }
}
