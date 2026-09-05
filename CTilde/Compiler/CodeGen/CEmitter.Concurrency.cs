namespace CTilde;

internal sealed partial class CEmitter
{
    private void EmitScalarAtomicSupport(CWriter writer)
    {
        writer.WriteLine("static int ct_atomic_order(int32_t order, bool load, bool store)");
        writer.WriteLine("{");
        writer.WriteLine("    if (order < 0 || order > 4 || (load && (order == 2 || order == 3)) || (store && (order == 1 || order == 3))) ct_raise_runtime_fault(CT_FAULT_ARGUMENT, \"CTA0002\", \"<atomic>\", 0);");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    (void)load; (void)store; return order;");
        writer.WriteLine("#else");
        writer.WriteLine("    static const int orders[5] = { __ATOMIC_RELAXED, __ATOMIC_ACQUIRE, __ATOMIC_RELEASE, __ATOMIC_ACQ_REL, __ATOMIC_SEQ_CST }; return orders[order];");
        writer.WriteLine("#endif");
        writer.WriteLine("}");
        writer.WriteLine("#if !defined(_MSC_VER)");
        writer.WriteLine("static uint32_t ct_atomic_scalar_compare_exchange_u32(void* storage, uint32_t desired, uint32_t comparand, int success, int failure)");
        writer.WriteLine("{");
        writer.WriteLine("    uint32_t expected = comparand;");
        writer.WriteLine(IsManagedModule ? "#if defined(ESP_PLATFORM)" : "#if 0");
        writer.WriteLine("    (void)success; (void)failure; (void)ctilde_managed_atomic_compare_exchange_u32((volatile uint32_t*)storage, &expected, desired);");
        writer.WriteLine("#else");
        writer.WriteLine("    (void)__atomic_compare_exchange_n((uint32_t*)storage, &expected, desired, false, success, failure);");
        writer.WriteLine("#endif");
        writer.WriteLine("    return expected;");
        writer.WriteLine("}");
        if (IsManagedModule)
        {
            writer.WriteLine("#if defined(ESP_PLATFORM)");
            writer.WriteLine("static uint32_t ct_atomic_scalar_compare_exchange_subword(void* storage, size_t size, uint32_t desired, uint32_t comparand)");
            writer.WriteLine("{");
            writer.WriteLine("    volatile uint32_t* word = (volatile uint32_t*)((uintptr_t)storage & ~(uintptr_t)3u);");
            writer.WriteLine("    unsigned shift = (unsigned)((uintptr_t)storage & 3u) * 8u; uint32_t value_mask = size == 1u ? UINT32_C(255) : UINT32_C(65535); uint32_t mask = value_mask << shift; comparand &= value_mask;");
            writer.WriteLine("    uint32_t expected = __atomic_load_n(word, __ATOMIC_SEQ_CST);");
            writer.WriteLine("    for (;;) { uint32_t observed = (expected & mask) >> shift; if (observed != comparand) return observed; uint32_t replacement = (expected & ~mask) | ((desired << shift) & mask); if (ctilde_managed_atomic_compare_exchange_u32(word, &expected, replacement)) return observed; }");
            writer.WriteLine("}");
            writer.WriteLine("#endif");
        }
        writer.WriteLine("#endif");
        writer.WriteLine("static uint64_t ct_atomic_scalar_load(const void* storage, size_t size, int32_t order)");
        writer.WriteLine("{");
        writer.WriteLine("    int native_order = ct_atomic_order(order, true, false);");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    (void)native_order; switch (size) { case 1u: return (uint8_t)_InterlockedCompareExchange8((volatile char*)storage, 0, 0); case 2u: return (uint16_t)_InterlockedCompareExchange16((volatile short*)storage, 0, 0); case 4u: return (uint32_t)_InterlockedCompareExchange((volatile long*)storage, 0, 0); case 8u: return (uint64_t)_InterlockedCompareExchange64((volatile __int64*)storage, 0, 0); default: ct_raise_runtime_fault(CT_FAULT_ARGUMENT, \"CTA0003\", \"<atomic>\", 0); }");
        writer.WriteLine("#else");
        writer.WriteLine("    switch (size) { case 1u: return __atomic_load_n((const uint8_t*)storage, native_order); case 2u: return __atomic_load_n((const uint16_t*)storage, native_order); case 4u: return __atomic_load_n((const uint32_t*)storage, native_order); case 8u: return __atomic_load_n((const uint64_t*)storage, native_order); default: ct_raise_runtime_fault(CT_FAULT_ARGUMENT, \"CTA0003\", \"<atomic>\", 0); }");
        writer.WriteLine("#endif");
        writer.WriteLine("}");
        writer.WriteLine("static uint64_t ct_atomic_scalar_compare_exchange(void* storage, size_t size, uint64_t desired, uint64_t comparand, int32_t success_order, int32_t failure_order)");
        writer.WriteLine("{");
        writer.WriteLine("    int success = ct_atomic_order(success_order, false, false); int failure = ct_atomic_order(failure_order, true, false); bool valid_failure = failure_order == 0 || (failure_order == 1 && (success_order == 1 || success_order == 3 || success_order == 4)) || (failure_order == 4 && success_order == 4); if (!valid_failure) ct_raise_runtime_fault(CT_FAULT_ARGUMENT, \"CTA0002\", \"<atomic>\", 0);");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    (void)success; (void)failure; switch (size) { case 1u: return (uint8_t)_InterlockedCompareExchange8((volatile char*)storage, (char)desired, (char)comparand); case 2u: return (uint16_t)_InterlockedCompareExchange16((volatile short*)storage, (short)desired, (short)comparand); case 4u: return (uint32_t)_InterlockedCompareExchange((volatile long*)storage, (long)desired, (long)comparand); case 8u: return (uint64_t)_InterlockedCompareExchange64((volatile __int64*)storage, (__int64)desired, (__int64)comparand); default: ct_raise_runtime_fault(CT_FAULT_ARGUMENT, \"CTA0003\", \"<atomic>\", 0); }");
        writer.WriteLine("#else");
        if (IsManagedModule)
        {
            writer.WriteLine("#if defined(ESP_PLATFORM)");
            writer.WriteLine("    if (size == 1u || size == 2u) return ct_atomic_scalar_compare_exchange_subword(storage, size, (uint32_t)desired, (uint32_t)comparand);");
            writer.WriteLine("#endif");
        }
        writer.WriteLine("    switch (size) { case 1u: { uint8_t expected = (uint8_t)comparand; (void)__atomic_compare_exchange_n((uint8_t*)storage, &expected, (uint8_t)desired, false, success, failure); return expected; } case 2u: { uint16_t expected = (uint16_t)comparand; (void)__atomic_compare_exchange_n((uint16_t*)storage, &expected, (uint16_t)desired, false, success, failure); return expected; } case 4u: return ct_atomic_scalar_compare_exchange_u32(storage, (uint32_t)desired, (uint32_t)comparand, success, failure); case 8u: { uint64_t expected = comparand; (void)__atomic_compare_exchange_n((uint64_t*)storage, &expected, desired, false, success, failure); return expected; } default: ct_raise_runtime_fault(CT_FAULT_ARGUMENT, \"CTA0003\", \"<atomic>\", 0); }");
        writer.WriteLine("#endif");
        writer.WriteLine("}");
        writer.WriteLine("static uint64_t ct_atomic_scalar_exchange(void* storage, size_t size, uint64_t desired, int32_t order) { (void)ct_atomic_order(order, false, false); uint64_t observed = ct_atomic_scalar_load(storage, size, 0); for (;;) { uint64_t previous = ct_atomic_scalar_compare_exchange(storage, size, desired, observed, order, 0); if (previous == observed) return observed; observed = previous; } }");
        writer.WriteLine("static void ct_atomic_scalar_store(void* storage, size_t size, uint64_t desired, int32_t order) { (void)ct_atomic_order(order, false, true); (void)ct_atomic_scalar_exchange(storage, size, desired, order); }");
        writer.WriteLine("static uint64_t ct_atomic_scalar_fetch(void* storage, size_t size, uint64_t operand, int32_t order, int operation) { uint64_t mask = size == 8u ? UINT64_MAX : (UINT64_C(1) << (unsigned)(size * 8u)) - 1u; uint64_t observed = ct_atomic_scalar_load(storage, size, 0); for (;;) { uint64_t desired = operation == 0 ? observed + operand : operation == 1 ? observed - operand : operation == 2 ? observed & operand : operation == 3 ? observed | operand : observed ^ operand; desired &= mask; uint64_t previous = ct_atomic_scalar_compare_exchange(storage, size, desired, observed, order, 0); if (previous == observed) return observed; observed = previous; } }");
        writer.WriteLine("static void ct_atomic_fence(int32_t order)");
        writer.WriteLine("{");
        writer.WriteLine("    int native_order = ct_atomic_order(order, false, false);");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    (void)native_order; MemoryBarrier(); _ReadWriteBarrier();");
        writer.WriteLine("#else");
        writer.WriteLine("    __atomic_thread_fence(native_order);");
        writer.WriteLine("#endif");
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitManagedThreadingSupport(CWriter writer)
    {
        if (!_usesManagedThreading)
            return;

        var thread = EmittedTypes.FirstOrDefault(type => type is { Namespace: "System.Threading", Name: "Thread" });
        var mutex = EmittedTypes.FirstOrDefault(type => type is { Namespace: "System.Threading", Name: "Mutex" });
        if (thread is not null)
            EmitManagedThreadSupport(writer, thread);
        if (mutex is not null)
            EmitManagedMutexSupport(writer, mutex);
    }

    private void EmitManagedThreadSupport(CWriter writer, TypeSymbol thread)
    {
        var typeName = NameMangler.Type(thread);
        string Field(string name) => thread.Fields.Single(field => field.Name == name).CName;
        var start = Field("start");
        var handle = Field("nativeHandle");
        var id = Field("runtimeId");
        var state = Field("state");
        var stack = Field("stackSize");
        var priority = Field("priority");

        if (IsFreestanding || UsesEspRuntimeThreads)
        {
            EmitRuntimeManagedThreadSupport(writer, thread, typeName, start, handle, id, state, stack, priority);
            return;
        }

        if (IsManagedModule)
        {
            writer.WriteLine("extern void* ctilde_managed_thread_payload_allocate(size_t size, void** done);");
            writer.WriteLine("extern void ctilde_managed_thread_payload_free(void* payload);");
            writer.WriteLine("CT_NORETURN extern void ctilde_managed_thread_exit(void);");
        }
        writer.WriteLine("typedef struct ct_managed_thread_payload {");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    HANDLE Handle; unsigned NativeId;");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine(IsManagedModule
            ? "    TaskHandle_t Handle; SemaphoreHandle_t Done; ct_process_context* Process;"
            : "    TaskHandle_t Handle; SemaphoreHandle_t Done;");
        writer.WriteLine("#else");
        writer.WriteLine("    pthread_t Handle; bool Joined;");
        writer.WriteLine("#endif");
        writer.WriteLine("    ct_atomic_u32 Ready; ct_atomic_u32 Abort;");
        writer.WriteLine("} ct_managed_thread_payload;");
        writer.WriteLine("static ct_atomic_u32 ct_managed_thread_next_id = CT_ATOMIC_U32_INIT(1u);");
        writer.WriteLine($"static void ct_managed_thread_complete({typeName}* thread)");
        writer.WriteLine("{");
        writer.WriteLine($"    ct_atomic_scalar_store((void*)&thread->{state}, sizeof(thread->{state}), UINT64_C(2), 2);");
        writer.WriteLine("    ct_release_fast((ct_object*)(void*)thread);");
        if (!IsManagedModule)
            writer.WriteLine("    ct_thread_detach();");
        writer.WriteLine("}");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("#define CT_MANAGED_THREAD_WORKER_RETURN unsigned __stdcall");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine("#define CT_MANAGED_THREAD_WORKER_RETURN void");
        writer.WriteLine("#else");
        writer.WriteLine("#define CT_MANAGED_THREAD_WORKER_RETURN void*");
        writer.WriteLine("#endif");
        writer.WriteLine("static CT_MANAGED_THREAD_WORKER_RETURN ct_managed_thread_worker(void* context)");
        writer.WriteLine("{");
        writer.WriteLine($"    {typeName}* thread = ({typeName}*)context;");
        writer.WriteLine($"    ct_managed_thread_payload* payload = (ct_managed_thread_payload*)(uintptr_t)thread->{handle};");
        if (IsManagedModule)
            writer.WriteLine("    ct_thread_attach_to(payload->Process);");
        else
            writer.WriteLine("    ct_thread_attach();");
        writer.WriteLine("    while (ct_atomic_load_acquire(&payload->Ready) == 0u) {");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("        SwitchToThread();");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine("        taskYIELD();");
        writer.WriteLine("#else");
        writer.WriteLine("        (void)sched_yield();");
        writer.WriteLine("#endif");
        writer.WriteLine("    }");
        writer.WriteLine("    if (ct_atomic_load_acquire(&payload->Abort) == 0u)");
        writer.WriteLine("    {");
        if (_usesExceptions)
        {
            writer.WriteLine("        ct_thread_state* worker_state = ct_thread_require_attached();");
            writer.WriteLine("        jmp_buf worker_jump;");
            writer.WriteLine("        ct_exception_frame worker_frame = { &worker_jump, worker_state->ExceptionTop, worker_state->CleanupTop };");
            writer.WriteLine("        worker_state->ExceptionTop = &worker_frame;");
            if (IsManagedModule)
            {
                writer.WriteLine("        volatile bool worker_failed = false;");
                writer.WriteLine("        if (setjmp(worker_jump) != 0) { static const uint8_t diagnostic[] = \"C~ unhandled child-thread exception\\n\"; ct_object* exception = worker_state->CurrentException; worker_state->CurrentException = NULL; worker_state->ExceptionTop = worker_frame.Previous; ct_runtime_console_write(diagnostic, sizeof(diagnostic) - 1u); ct_release(exception); worker_failed = true; }");
            }
            else
                writer.WriteLine("        if (setjmp(worker_jump) != 0) { ct_object* exception = worker_state->CurrentException; worker_state->CurrentException = NULL; worker_state->ExceptionTop = worker_frame.Previous; ct_unhandled_exception(exception); }");
        }
        if (_usesExceptions && IsManagedModule)
        {
            writer.WriteLine("        if (!worker_failed) {");
            writer.WriteLine($"            (void)ct_require_nonnull(thread->{start}, \"<thread-start>\", 0);");
            writer.WriteLine($"            thread->{start}->ct_invoke(thread->{start}->ct_target);");
            writer.WriteLine("        }");
        }
        else
        {
            writer.WriteLine($"        (void)ct_require_nonnull(thread->{start}, \"<thread-start>\", 0);");
            writer.WriteLine($"        thread->{start}->ct_invoke(thread->{start}->ct_target);");
        }
        if (_usesExceptions)
            writer.WriteLine("        worker_state->ExceptionTop = worker_frame.Previous;");
        writer.WriteLine("    }");
        writer.WriteLine("#if defined(ESP_PLATFORM)");
        writer.WriteLine("    (void)xSemaphoreGive(payload->Done);");
        writer.WriteLine("#endif");
        writer.WriteLine("    ct_managed_thread_complete(thread);");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    return 0u;");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine(IsManagedModule ? "    ctilde_managed_thread_exit();" : "    vTaskDelete(NULL);");
        writer.WriteLine("#else");
        writer.WriteLine("    return NULL;");
        writer.WriteLine("#endif");
        writer.WriteLine("}");
        writer.WriteLine("#undef CT_MANAGED_THREAD_WORKER_RETURN");

        writer.WriteLine($"static void ct_managed_thread_start({typeName}* thread)");
        writer.WriteLine("{");
        writer.WriteLine($"    thread = ({typeName}*)ct_require_nonnull(thread, \"<thread-start>\", 0); ct_runtime_require_ready();");
        writer.WriteLine($"    uint64_t prior = ct_atomic_scalar_compare_exchange((void*)&thread->{state}, sizeof(thread->{state}), UINT64_C(1), UINT64_C(0), 3, 0);");
        writer.WriteLine("    if (prior != 0u) ct_raise_runtime_fault(CT_FAULT_THREAD_STATE, \"CTT0101\", \"<thread-start>\", 0);");
        writer.WriteLine($"    if (thread->{start} == NULL) {{ ct_atomic_scalar_store((void*)&thread->{state}, sizeof(thread->{state}), 0u, 2); ct_raise_runtime_fault(CT_FAULT_ARGUMENT, \"CTT0102\", \"<thread-start>\", 0); }}");
        writer.WriteLine($"    if ((uint32_t)thread->{priority} > 4u) {{ ct_atomic_scalar_store((void*)&thread->{state}, sizeof(thread->{state}), 0u, 2); ct_raise_runtime_fault(CT_FAULT_THREAD_STATE, \"CTT0103\", \"<thread-start>\", 0); }}");
        if (IsManagedModule)
            writer.WriteLine("    void* done = NULL; ct_managed_thread_payload* payload = (ct_managed_thread_payload*)ctilde_managed_thread_payload_allocate(sizeof(*payload), &done); if (payload != NULL) payload->Done = (SemaphoreHandle_t)done;");
        else
            writer.WriteLine("    ct_managed_thread_payload* payload = (ct_managed_thread_payload*)calloc(1u, sizeof(*payload));");
        writer.WriteLine($"    if (payload == NULL) {{ ct_atomic_scalar_store((void*)&thread->{state}, sizeof(thread->{state}), 0u, 2); ct_raise_runtime_fault(CT_FAULT_OUT_OF_MEMORY, \"CTM0001\", \"<thread-start>\", 0); }}");
        writer.WriteLine("    ct_atomic_store_relaxed(&payload->Ready, 0u); ct_atomic_store_relaxed(&payload->Abort, 0u);");
        writer.WriteLine($"    thread->{handle} = (uintptr_t)(void*)payload; thread->{id} = ct_atomic_fetch_add_relaxed(&ct_managed_thread_next_id, 1u); ct_retain_fast((ct_object*)(void*)thread);");
        writer.WriteLine("    bool created = false; bool priority_ok = true;");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine($"    uintptr_t native = _beginthreadex(NULL, (unsigned)thread->{stack}, ct_managed_thread_worker, thread, CREATE_SUSPENDED, &payload->NativeId);");
        writer.WriteLine("    if (native != 0u) { payload->Handle = (HANDLE)native; created = true; static const int priorities[5] = { THREAD_PRIORITY_LOWEST, THREAD_PRIORITY_BELOW_NORMAL, THREAD_PRIORITY_NORMAL, THREAD_PRIORITY_ABOVE_NORMAL, THREAD_PRIORITY_HIGHEST }; priority_ok = SetThreadPriority(payload->Handle, priorities[(unsigned)thread->" + priority + " < 5u ? (unsigned)thread->" + priority + " : 2u]) != 0; if (priority_ok) { ct_atomic_store_release(&payload->Ready, 1u); (void)ResumeThread(payload->Handle); } }");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine($"    uint32_t stack_bytes = thread->{stack}; uint32_t native_stack_bytes = stack_bytes == 0u ? (uint32_t)configMINIMAL_STACK_SIZE : (stack_bytes + 3u) & ~UINT32_C(3); UBaseType_t native_priority = (UBaseType_t)(tskIDLE_PRIORITY + 1u + (unsigned)thread->{priority});");
        if (IsManagedModule)
            writer.WriteLine("    payload->Process = ct_runtime_api->CurrentProcess();");
        if (!IsManagedModule)
            writer.WriteLine("    payload->Done = xSemaphoreCreateBinary();");
        writer.WriteLine("    if (payload->Done != NULL) { ct_atomic_store_release(&payload->Ready, 1u); created = xTaskCreate(ct_managed_thread_worker, \"C~ worker\", native_stack_bytes, thread, native_priority, &payload->Handle) == pdPASS; if (!created) ct_atomic_store_relaxed(&payload->Ready, 0u); }");
        writer.WriteLine("#else");
        writer.WriteLine("    pthread_attr_t attributes; if (pthread_attr_init(&attributes) == 0) {");
        writer.WriteLine($"        if (thread->{stack} != 0u && pthread_attr_setstacksize(&attributes, (size_t)thread->{stack}) != 0) priority_ok = false;");
        writer.WriteLine("        if (priority_ok && pthread_create(&payload->Handle, &attributes, ct_managed_thread_worker, thread) == 0) { created = true; }");
        writer.WriteLine("        (void)pthread_attr_destroy(&attributes);");
        writer.WriteLine($"        if (created && thread->{priority} != 2) {{ int policy = 0; struct sched_param parameter; if (pthread_getschedparam(payload->Handle, &policy, &parameter) != 0) priority_ok = false; else {{ int low = sched_get_priority_min(policy), high = sched_get_priority_max(policy); int requested = low + (int)((int64_t)(high - low) * (int64_t)thread->{priority} / 4); parameter.sched_priority = requested; priority_ok = pthread_setschedparam(payload->Handle, policy, &parameter) == 0; }} }}");
        writer.WriteLine("        if (created) { if (!priority_ok) ct_atomic_store_release(&payload->Abort, 1u); ct_atomic_store_release(&payload->Ready, 1u); }");
        writer.WriteLine("    }");
        writer.WriteLine("#endif");
        writer.WriteLine("    if (!created || !priority_ok) {");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("        if (created) { (void)TerminateThread(payload->Handle, 1u); (void)CloseHandle(payload->Handle); created = false; }");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        if (!IsManagedModule)
            writer.WriteLine("        if (!created && payload->Done != NULL) vSemaphoreDelete(payload->Done);");
        writer.WriteLine("#else");
        writer.WriteLine("        if (created) (void)pthread_join(payload->Handle, NULL);");
        writer.WriteLine("#endif");
        var freePayload = IsManagedModule ? "ctilde_managed_thread_payload_free" : "free";
        writer.WriteLine($"        if (!created) {{ thread->{handle} = 0u; {freePayload}(payload); ct_release_fast((ct_object*)(void*)thread); ct_atomic_scalar_store((void*)&thread->{state}, sizeof(thread->{state}), 0u, 2); }}");
        writer.WriteLine("        ct_raise_runtime_fault(CT_FAULT_THREAD_STATE, \"CTT0103\", \"<thread-start>\", 0);");
        writer.WriteLine("    }");
        writer.WriteLine("}");

        writer.WriteLine($"static void ct_managed_thread_join({typeName}* thread)");
        writer.WriteLine("{");
        writer.WriteLine($"    thread = ({typeName}*)ct_require_nonnull(thread, \"<thread-join>\", 0); uint64_t state = ct_atomic_scalar_load((void*)&thread->{state}, sizeof(thread->{state}), 1); if (state == 0u) ct_raise_runtime_fault(CT_FAULT_THREAD_STATE, \"CTT0104\", \"<thread-join>\", 0); ct_managed_thread_payload* payload = (ct_managed_thread_payload*)(uintptr_t)thread->{handle};");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    if (GetCurrentThreadId() == payload->NativeId) ct_raise_runtime_fault(CT_FAULT_THREAD_STATE, \"CTT0105\", \"<thread-join>\", 0); if (WaitForSingleObject(payload->Handle, INFINITE) != WAIT_OBJECT_0) ct_raise_runtime_fault(CT_FAULT_THREAD_STATE, \"CTT0106\", \"<thread-join>\", 0);");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine("    if (xTaskGetCurrentTaskHandle() == payload->Handle) { ct_raise_runtime_fault(CT_FAULT_THREAD_STATE, \"CTT0105\", \"<thread-join>\", 0); }");
        writer.WriteLine("    (void)xSemaphoreTake(payload->Done, portMAX_DELAY); (void)xSemaphoreGive(payload->Done);");
        writer.WriteLine("#else");
        writer.WriteLine("    if (pthread_equal(pthread_self(), payload->Handle)) { ct_raise_runtime_fault(CT_FAULT_THREAD_STATE, \"CTT0105\", \"<thread-join>\", 0); }");
        writer.WriteLine("    if (!payload->Joined) { if (pthread_join(payload->Handle, NULL) != 0) { ct_raise_runtime_fault(CT_FAULT_THREAD_STATE, \"CTT0106\", \"<thread-join>\", 0); } payload->Joined = true; }");
        writer.WriteLine("#endif");
        writer.WriteLine("    ct_atomic_acquire_fence();");
        writer.WriteLine("}");
        writer.WriteLine("static void ct_managed_thread_sleep(uint32_t milliseconds) {");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    Sleep(milliseconds);");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine("    TickType_t ticks = pdMS_TO_TICKS(milliseconds); if (milliseconds != 0u && ticks == 0u) ticks = 1u; vTaskDelay(ticks);");
        writer.WriteLine("#else");
        writer.WriteLine("    struct timespec duration = { (time_t)(milliseconds / 1000u), (long)(milliseconds % 1000u) * 1000000L }; while (nanosleep(&duration, &duration) != 0 && errno == EINTR) { }");
        writer.WriteLine("#endif");
        writer.WriteLine("}");
        writer.WriteLine("static void ct_managed_thread_yield(void) {");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    (void)SwitchToThread();");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine("    taskYIELD();");
        writer.WriteLine("#else");
        writer.WriteLine("    (void)sched_yield();");
        writer.WriteLine("#endif");
        writer.WriteLine("}");
        writer.WriteLine("static void ct_managed_thread_drop(ct_object* object) {");
        writer.WriteLine($"    {typeName}* thread = ({typeName}*)(void*)object; ct_managed_thread_payload* payload = (ct_managed_thread_payload*)(uintptr_t)thread->{handle}; if (payload == NULL) return;");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    (void)CloseHandle(payload->Handle);");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        if (!IsManagedModule)
            writer.WriteLine("    if (payload->Done != NULL) vSemaphoreDelete(payload->Done);");
        writer.WriteLine("#else");
        writer.WriteLine("    if (!payload->Joined) (void)pthread_detach(payload->Handle);");
        writer.WriteLine("#endif");
        writer.WriteLine($"    thread->{handle} = 0u; {freePayload}(payload);");
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitManagedMutexSupport(CWriter writer, TypeSymbol mutex)
    {
        var typeName = NameMangler.Type(mutex);
        var handle = mutex.Fields.Single(field => field.Name == "nativeHandle").CName;
        if (IsFreestanding || UsesEspRuntimeThreads)
        {
            EmitRuntimeManagedMutexSupport(writer, typeName, handle);
            return;
        }
        writer.WriteLine("typedef struct ct_managed_mutex_payload {");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    CRITICAL_SECTION Mutex; DWORD Owner;");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine("    SemaphoreHandle_t Mutex; TaskHandle_t Owner;");
        writer.WriteLine("#else");
        writer.WriteLine("    pthread_mutex_t Mutex; pthread_t Owner; bool HasOwner;");
        writer.WriteLine("#endif");
        writer.WriteLine("    uint32_t Depth;");
        writer.WriteLine("} ct_managed_mutex_payload;");
        writer.WriteLine($"static ct_managed_mutex_payload* ct_managed_mutex_payload_for({typeName}* mutex)");
        writer.WriteLine("{");
        writer.WriteLine($"    mutex = ({typeName}*)ct_require_nonnull(mutex, \"<mutex>\", 0); ct_managed_mutex_payload* payload = (ct_managed_mutex_payload*)(uintptr_t)mutex->{handle}; if (payload != NULL) return payload; payload = (ct_managed_mutex_payload*)calloc(1u, sizeof(*payload)); if (payload == NULL) ct_raise_runtime_fault(CT_FAULT_OUT_OF_MEMORY, \"CTM0001\", \"<mutex>\", 0);");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    InitializeCriticalSection(&payload->Mutex);");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine("    payload->Mutex = xSemaphoreCreateRecursiveMutex(); if (payload->Mutex == NULL) { free(payload); ct_raise_runtime_fault(CT_FAULT_OUT_OF_MEMORY, \"CTM0001\", \"<mutex>\", 0); }");
        writer.WriteLine("#else");
        writer.WriteLine("    pthread_mutexattr_t attributes; if (pthread_mutexattr_init(&attributes) != 0 || pthread_mutexattr_settype(&attributes, PTHREAD_MUTEX_RECURSIVE) != 0 || pthread_mutex_init(&payload->Mutex, &attributes) != 0) { (void)pthread_mutexattr_destroy(&attributes); free(payload); ct_raise_runtime_fault(CT_FAULT_THREAD_STATE, \"CTT0110\", \"<mutex>\", 0); } (void)pthread_mutexattr_destroy(&attributes);");
        writer.WriteLine("#endif");
        writer.WriteLine($"    uint64_t observed = ct_atomic_scalar_compare_exchange((void*)&mutex->{handle}, sizeof(mutex->{handle}), (uint64_t)(uintptr_t)(void*)payload, 0u, 3, 0); if (observed == 0u) return payload;");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    DeleteCriticalSection(&payload->Mutex);");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine("    vSemaphoreDelete(payload->Mutex);");
        writer.WriteLine("#else");
        writer.WriteLine("    (void)pthread_mutex_destroy(&payload->Mutex);");
        writer.WriteLine("#endif");
        writer.WriteLine("    free(payload); return (ct_managed_mutex_payload*)(uintptr_t)observed;");
        writer.WriteLine("}");
        writer.WriteLine($"static void ct_managed_mutex_enter({typeName}* mutex) {{ ct_managed_mutex_payload* payload = ct_managed_mutex_payload_for(mutex);");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    EnterCriticalSection(&payload->Mutex); payload->Owner = GetCurrentThreadId();");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine("    (void)xSemaphoreTakeRecursive(payload->Mutex, portMAX_DELAY); payload->Owner = xTaskGetCurrentTaskHandle();");
        writer.WriteLine("#else");
        writer.WriteLine("    if (pthread_mutex_lock(&payload->Mutex) != 0) { ct_raise_runtime_fault(CT_FAULT_THREAD_STATE, \"CTT0111\", \"<mutex-enter>\", 0); }");
        writer.WriteLine("    payload->Owner = pthread_self(); payload->HasOwner = true;");
        writer.WriteLine("#endif");
        writer.WriteLine("    ++payload->Depth; ct_atomic_acquire_fence(); }");
        writer.WriteLine($"static bool ct_managed_mutex_try_enter({typeName}* mutex) {{ ct_managed_mutex_payload* payload = ct_managed_mutex_payload_for(mutex); bool entered = false;");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    entered = TryEnterCriticalSection(&payload->Mutex) != 0; if (entered) payload->Owner = GetCurrentThreadId();");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine("    entered = xSemaphoreTakeRecursive(payload->Mutex, 0u) == pdTRUE; if (entered) payload->Owner = xTaskGetCurrentTaskHandle();");
        writer.WriteLine("#else");
        writer.WriteLine("    entered = pthread_mutex_trylock(&payload->Mutex) == 0; if (entered) { payload->Owner = pthread_self(); payload->HasOwner = true; }");
        writer.WriteLine("#endif");
        writer.WriteLine("    if (entered) { ++payload->Depth; ct_atomic_acquire_fence(); } return entered; }");
        writer.WriteLine($"static void ct_managed_mutex_exit({typeName}* mutex) {{ ct_managed_mutex_payload* payload = ct_managed_mutex_payload_for(mutex); bool owner = false;");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    owner = payload->Depth != 0u && payload->Owner == GetCurrentThreadId();");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine("    owner = payload->Depth != 0u && payload->Owner == xTaskGetCurrentTaskHandle();");
        writer.WriteLine("#else");
        writer.WriteLine("    owner = payload->Depth != 0u && payload->HasOwner && pthread_equal(payload->Owner, pthread_self());");
        writer.WriteLine("#endif");
        writer.WriteLine("    if (!owner) { ct_raise_runtime_fault(CT_FAULT_SYNCHRONIZATION_LOCK, \"CTT0112\", \"<mutex-exit>\", 0); }");
        writer.WriteLine("    ct_atomic_fence(2); --payload->Depth;");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    if (payload->Depth == 0u) payload->Owner = 0u; LeaveCriticalSection(&payload->Mutex);");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine("    if (payload->Depth == 0u) { payload->Owner = NULL; }");
        writer.WriteLine("    (void)xSemaphoreGiveRecursive(payload->Mutex);");
        writer.WriteLine("#else");
        writer.WriteLine("    if (payload->Depth == 0u) { payload->HasOwner = false; } (void)pthread_mutex_unlock(&payload->Mutex);");
        writer.WriteLine("#endif");
        writer.WriteLine("}");
        writer.WriteLine("static void ct_managed_mutex_drop(ct_object* object) {");
        writer.WriteLine($"    {typeName}* mutex = ({typeName}*)(void*)object; ct_managed_mutex_payload* payload = (ct_managed_mutex_payload*)(uintptr_t)mutex->{handle}; if (payload == NULL) return; if (payload->Depth != 0u) ct_fail(\"CTT0002\", \"<mutex-drop>\", 0);");
        writer.WriteLine("#if defined(_MSC_VER)");
        writer.WriteLine("    DeleteCriticalSection(&payload->Mutex);");
        writer.WriteLine("#elif defined(ESP_PLATFORM)");
        writer.WriteLine("    vSemaphoreDelete(payload->Mutex);");
        writer.WriteLine("#else");
        writer.WriteLine("    (void)pthread_mutex_destroy(&payload->Mutex);");
        writer.WriteLine("#endif");
        writer.WriteLine($"    mutex->{handle} = 0u; free(payload);");
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private void EmitRuntimeManagedThreadSupport(CWriter writer, TypeSymbol thread, string typeName,
        string start, string handle, string id, string state, string stack, string priority)
    {
        var result = Model.Types["System.Runtime.RuntimeResult"];
        string F(string name) => result.Fields.Single(field => field.Name == name).CAccessPath;
        var resultType = CTypeName(result.Type);
        var create = Model.RuntimeImplementations[RuntimeImplementationRole.ThreadCreate];
        var join = Model.RuntimeImplementations[RuntimeImplementationRole.ThreadJoin];
        var close = Model.RuntimeImplementations[RuntimeImplementationRole.ThreadClose];
        var sleep = Model.RuntimeImplementations[RuntimeImplementationRole.ThreadSleep];
        var yield = Model.RuntimeImplementations[RuntimeImplementationRole.ThreadYield];
        var priorityType = CTypeName(Model.Types["System.Runtime.RuntimeThreadPriority"].Type);
        writer.WriteLine($"static void ct_runtime_thread_check({resultType} result, const char* code) {{ if ((uint8_t)result.{F("Status")} != 0u) ct_runtime_service_fail(code, (uint8_t)result.{F("Status")}, result.{F("NativeCode")}); }}");
        writer.WriteLine($"static void ct_managed_thread_worker(void* context) {{ {typeName}* thread = ({typeName}*)context; ct_thread_state worker_state; (void)ct_memset(&worker_state, 0, sizeof(worker_state)); ct_thread_set_current(&worker_state); (void)ct_require_nonnull(thread->{start}, \"<thread-start>\", 0); thread->{start}->ct_invoke(thread->{start}->ct_target); ct_atomic_scalar_store((void*)&thread->{state}, sizeof(thread->{state}), UINT64_C(2), 2); ct_thread_set_current(NULL); ct_release_fast((ct_object*)(void*)thread); }}");
        writer.WriteLine($"static void ct_managed_thread_start({typeName}* thread) {{ thread = ({typeName}*)ct_require_nonnull(thread, \"<thread-start>\", 0); ct_runtime_require_ready(); uint64_t prior = ct_atomic_scalar_compare_exchange((void*)&thread->{state}, sizeof(thread->{state}), UINT64_C(1), UINT64_C(0), 3, 0); if (prior != 0u) ct_raise_runtime_fault(CT_FAULT_THREAD_STATE, \"CTT0101\", \"<thread-start>\", 0); if (thread->{start} == NULL) {{ ct_atomic_scalar_store((void*)&thread->{state}, sizeof(thread->{state}), 0u, 2); ct_raise_runtime_fault(CT_FAULT_ARGUMENT, \"CTT0102\", \"<thread-start>\", 0); }} uintptr_t native_handle = 0u; uint32_t runtime_id = 0u; ct_retain_fast((ct_object*)(void*)thread); {resultType} result = {create.CName}(ct_managed_thread_worker, thread, thread->{stack}, ({priorityType})thread->{priority}, &native_handle, &runtime_id); if ((uint8_t)result.{F("Status")} != 0u || native_handle == 0u) {{ ct_release_fast((ct_object*)(void*)thread); ct_atomic_scalar_store((void*)&thread->{state}, sizeof(thread->{state}), 0u, 2); if ((uint8_t)result.{F("Status")} != 0u) ct_runtime_service_fail(\"CTT0103\", (uint8_t)result.{F("Status")}, result.{F("NativeCode")}); ct_runtime_service_fail(\"CTT0103\", UINT8_C(11), 0); }} thread->{handle} = native_handle; thread->{id} = runtime_id; }}");
        writer.WriteLine($"static void ct_managed_thread_join({typeName}* thread) {{ thread = ({typeName}*)ct_require_nonnull(thread, \"<thread-join>\", 0); if (ct_atomic_scalar_load((void*)&thread->{state}, sizeof(thread->{state}), 1) == 0u) ct_raise_runtime_fault(CT_FAULT_THREAD_STATE, \"CTT0104\", \"<thread-join>\", 0); ct_runtime_thread_check({join.CName}(thread->{handle}), \"CTT0106\"); ct_atomic_acquire_fence(); }}");
        writer.WriteLine($"static void ct_managed_thread_sleep(uint32_t milliseconds) {{ ct_runtime_thread_check({sleep.CName}(milliseconds), \"CTT0107\"); }}");
        writer.WriteLine($"static void ct_managed_thread_yield(void) {{ ct_runtime_thread_check({yield.CName}(), \"CTT0108\"); }}");
        writer.WriteLine($"static void ct_managed_thread_drop(ct_object* object) {{ {typeName}* thread = ({typeName}*)(void*)object; uintptr_t native_handle = thread->{handle}; if (native_handle == 0u) return; thread->{handle} = 0u; ct_runtime_thread_check({close.CName}(native_handle), \"CTT0109\"); }}");
        writer.WriteLine();
    }

    private void EmitRuntimeManagedMutexSupport(CWriter writer, string typeName, string handle)
    {
        var result = Model.Types["System.Runtime.RuntimeResult"];
        string F(string name) => result.Fields.Single(field => field.Name == name).CAccessPath;
        var resultType = CTypeName(result.Type);
        var create = Model.RuntimeImplementations[RuntimeImplementationRole.MutexCreate];
        var enter = Model.RuntimeImplementations[RuntimeImplementationRole.MutexEnter];
        var tryEnter = Model.RuntimeImplementations[RuntimeImplementationRole.MutexTryEnter];
        var exit = Model.RuntimeImplementations[RuntimeImplementationRole.MutexExit];
        var close = Model.RuntimeImplementations[RuntimeImplementationRole.MutexClose];
        writer.WriteLine($"static void ct_runtime_mutex_check({resultType} result, const char* code) {{ if ((uint8_t)result.{F("Status")} != 0u) ct_runtime_service_fail(code, (uint8_t)result.{F("Status")}, result.{F("NativeCode")}); }}");
        writer.WriteLine($"static uintptr_t ct_managed_mutex_handle({typeName}* mutex) {{ mutex = ({typeName}*)ct_require_nonnull(mutex, \"<mutex>\", 0); uintptr_t value = (uintptr_t)ct_atomic_scalar_load((void*)&mutex->{handle}, sizeof(mutex->{handle}), 1); if (value != 0u) return value; uintptr_t created = 0u; ct_runtime_mutex_check({create.CName}(&created), \"CTT0110\"); if (created == 0u) ct_runtime_service_fail(\"CTT0110\", UINT8_C(11), 0); uint64_t observed = ct_atomic_scalar_compare_exchange((void*)&mutex->{handle}, sizeof(mutex->{handle}), (uint64_t)created, 0u, 3, 0); if (observed == 0u) return created; ct_runtime_mutex_check({close.CName}(created), \"CTT0110\"); return (uintptr_t)observed; }}");
        writer.WriteLine($"static void ct_managed_mutex_enter({typeName}* mutex) {{ ct_runtime_mutex_check({enter.CName}(ct_managed_mutex_handle(mutex)), \"CTT0111\"); ct_atomic_acquire_fence(); }}");
        writer.WriteLine($"static bool ct_managed_mutex_try_enter({typeName}* mutex) {{ bool entered = false; ct_runtime_mutex_check({tryEnter.CName}(ct_managed_mutex_handle(mutex), &entered), \"CTT0111\"); if (entered) ct_atomic_acquire_fence(); return entered; }}");
        writer.WriteLine($"static void ct_managed_mutex_exit({typeName}* mutex) {{ ct_atomic_fence(2); ct_runtime_mutex_check({exit.CName}(ct_managed_mutex_handle(mutex)), \"CTT0112\"); }}");
        writer.WriteLine($"static void ct_managed_mutex_drop(ct_object* object) {{ {typeName}* mutex = ({typeName}*)(void*)object; uintptr_t value = mutex->{handle}; if (value == 0u) return; mutex->{handle} = 0u; ct_runtime_mutex_check({close.CName}(value), \"CTT0113\"); }}");
        writer.WriteLine();
    }
}
