import { existsSync, mkdirSync, readFileSync, rmSync } from 'fs';
import { createHash } from 'crypto';
import * as path from 'path';
import * as vscode from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
} from 'vscode-languageclient/node';
import {
    DevelopmentServerWatchManager,
    resolveServerLaunch,
    RestartCoordinator,
    ServerLaunchConfiguration,
    serverPathError,
    stageExternalServer,
} from './serverDevelopment';
import {
    compilerArguments,
    compilerPathError,
    CTildeProjectTarget,
    CTildeTaskMode,
    findNearestProject,
    resolveCompilerLaunch,
    resolveDebugProjectPath,
    resolveTaskProjectPath,
} from './projectBuild';

let controller: LanguageServerController | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    const provider = new StandardLibraryContentProvider();
    controller = new LanguageServerController(context, provider);
    const buildProvider = new CTildeTaskProvider(context);
    const debugProvider = new CTildeDebugProjectProvider(context, buildProvider);
    context.subscriptions.push(
        vscode.workspace.registerTextDocumentContentProvider('ctilde-stdlib', provider),
        vscode.commands.registerCommand('ctilde.languageServer.restart', () => controller?.restart()),
        vscode.commands.registerCommand('ctilde.languageServer.showOutput', () => controller?.showOutput()),
        vscode.commands.registerCommand('ctilde.project.check', () => buildProvider.runProject('check')),
        vscode.commands.registerCommand('ctilde.project.build', () => buildProvider.runProject('build')),
        vscode.commands.registerCommand('ctilde.project.debug', () => debugProvider.runProject('launch')),
        vscode.commands.registerCommand('ctilde.project.attach', () => debugProvider.runProject('attach')),
        vscode.tasks.registerTaskProvider('ctilde', buildProvider),
        vscode.debug.registerDebugConfigurationProvider('ctilde', debugProvider),
        vscode.workspace.onDidChangeConfiguration(event => controller?.configurationChanged(event)),
    );
    await controller.start();
}

interface CTildeTaskDefinition extends vscode.TaskDefinition {
    readonly type: 'ctilde';
    readonly project: string;
    readonly mode: CTildeTaskMode;
}

class CTildeTaskProvider implements vscode.TaskProvider {
    public constructor(private readonly context: vscode.ExtensionContext) {
    }

    public async provideTasks(): Promise<vscode.Task[]> {
        const manifests = await this.discoverProjects();
        return manifests.flatMap(manifest => [this.createTask(manifest, 'build'), this.createTask(manifest, 'check')]);
    }

    public resolveTask(task: vscode.Task): vscode.Task | undefined {
        const definition = task.definition as Partial<CTildeTaskDefinition>;
        if (typeof definition.project !== 'string' || (definition.mode !== 'build' && definition.mode !== 'check'))
            return undefined;
        const folder = typeof task.scope === 'object' ? task.scope : undefined;
        try {
            const project = resolveTaskProjectPath(definition.project, folder?.uri.fsPath);
            return this.createTask(project, definition.mode, folder);
        } catch (error) {
            void vscode.window.showErrorMessage(String(error instanceof Error ? error.message : error));
            return undefined;
        }
    }

    public async runProject(mode: CTildeTaskMode): Promise<void> {
        if (!await vscode.workspace.saveAll(false))
            return;
        const project = await this.selectProject();
        if (project === undefined)
            return;
        let task: vscode.Task;
        try {
            task = this.createTask(project, mode);
            const launch = this.readCompilerLaunch();
            const error = compilerPathError(launch, existsSync);
            if (error !== undefined) {
                void vscode.window.showErrorMessage(error);
                return;
            }
        } catch (error) {
            void vscode.window.showErrorMessage(String(error instanceof Error ? error.message : error));
            return;
        }
        await vscode.tasks.executeTask(task);
    }

    private createTask(project: string, mode: CTildeTaskMode, suppliedFolder?: vscode.WorkspaceFolder): vscode.Task {
        const uri = vscode.Uri.file(project);
        const folder = suppliedFolder ?? vscode.workspace.getWorkspaceFolder(uri);
        const launch = this.readCompilerLaunch();
        const configuration = vscode.workspace.getConfiguration('ctilde.compiler');
        const target = this.readProjectTarget(project);
        const args = compilerArguments(launch, project, mode, target, {
            nativeCompiler: configuration.get<string>('nativeCompiler', ''),
            idfPath: configuration.get<string>('idfPath', ''),
        });
        const definition: CTildeTaskDefinition = { type: 'ctilde', project, mode };
        const projectName = path.basename(path.dirname(project));
        const label = `${mode === 'build' ? 'Build' : 'Check'} ${projectName}`;
        const execution = new vscode.ProcessExecution(launch.command, args, { cwd: path.dirname(project) });
        const task = new vscode.Task(definition, folder ?? vscode.TaskScope.Workspace, label, 'C~', execution,
            ['$ctilde', '$gcc', '$msCompile']);
        task.group = mode === 'build' ? vscode.TaskGroup.Build : vscode.TaskGroup.Test;
        task.runOptions = { reevaluateOnRerun: true, instanceLimit: 1 } as vscode.RunOptions & { instanceLimit: number };
        return task;
    }

    public readCompilerLaunch() {
        const configuration = vscode.workspace.getConfiguration('ctilde.compiler');
        const compilerPath = configuration.get<string>('compilerPath', '');
        const dotnetPath = configuration.get<string>('dotnetPath', 'dotnet');
        const workspacePath = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
        return resolveCompilerLaunch(compilerPath, dotnetPath, this.context.extensionPath, workspacePath);
    }

    public readProjectTarget(project: string): CTildeProjectTarget {
        try {
            const document = JSON.parse(readFileSync(project, 'utf8')) as { target?: unknown };
            return document.target === 'esp-idf' ? 'esp-idf' : document.target === undefined || document.target === 'hosted' ? 'hosted' : 'unknown';
        } catch {
            return 'unknown';
        }
    }

    private async discoverProjects(): Promise<string[]> {
        const uris = await vscode.workspace.findFiles('**/ctilde.json',
            '**/{.git,bin,obj,build,node_modules,managed_components}/**');
        return uris.map(uri => uri.fsPath).sort((left, right) => left.localeCompare(right));
    }

    public async selectProject(): Promise<string | undefined> {
        const active = vscode.window.activeTextEditor?.document;
        if (active?.uri.scheme === 'file' && active.languageId === 'ctilde') {
            const nearest = findNearestProject(active.uri.fsPath, existsSync);
            if (nearest !== undefined && vscode.workspace.getWorkspaceFolder(vscode.Uri.file(nearest)) !== undefined)
                return nearest;
        }

        const projects = await this.discoverProjects();
        if (projects.length === 0) {
            void vscode.window.showErrorMessage('No ctilde.json project was found in this workspace.');
            return undefined;
        }
        if (projects.length === 1)
            return projects[0];
        const choices = projects.map(project => ({
            label: path.basename(path.dirname(project)),
            description: vscode.workspace.asRelativePath(project),
            project,
        }));
        return (await vscode.window.showQuickPick(choices, { placeHolder: 'Select the C~ project to build' }))?.project;
    }
}

interface PreparedDebugTarget {
    readonly target: CTildeProjectTarget;
    readonly backend: 'gdb' | 'msvc';
    readonly program: string;
    readonly sourceRoot: string;
    readonly workingDirectory: string;
    readonly serialPort?: string;
    readonly baudRate?: number;
}

class CTildeDebugProjectProvider implements vscode.DebugConfigurationProvider {
    public constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly projects: CTildeTaskProvider,
    ) {
    }

    public async runProject(request: 'launch' | 'attach'): Promise<void> {
        if (request === 'launch' && !await vscode.workspace.saveAll(false))
            return;
        const project = await this.projects.selectProject();
        if (project === undefined)
            return;
        const folder = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(project));
        const configuration = await this.prepare(project, request,
            { type: 'ctilde', request, name: request === 'launch' ? 'Debug C~ Project' : 'Attach C~ Debugger' }, true);
        if (configuration !== undefined)
            await vscode.debug.startDebugging(folder, configuration);
    }

    public async resolveDebugConfiguration(
        folder: vscode.WorkspaceFolder | undefined,
        configuration: vscode.DebugConfiguration,
    ): Promise<vscode.DebugConfiguration | undefined> {
        if (typeof configuration.debugTarget === 'string')
            return configuration;
        if (typeof configuration.project !== 'string' || configuration.project.trim().length === 0) {
            void vscode.window.showErrorMessage('A C~ debug configuration requires a ctilde.json project path.');
            return undefined;
        }
        let project: string;
        try {
            project = resolveDebugProjectPath(configuration.project,
                folder?.uri.fsPath ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath);
        } catch (error) {
            void vscode.window.showErrorMessage(String(error instanceof Error ? error.message : error));
            return undefined;
        }
        if (configuration.request === 'launch' && !await vscode.workspace.saveAll(false))
            return undefined;
        return this.prepare(project, configuration.request === 'attach' ? 'attach' : 'launch', configuration, false);
    }

    private async prepare(
        project: string,
        request: 'launch' | 'attach',
        supplied: vscode.DebugConfiguration,
        allowMsvcFallback: boolean,
    ): Promise<vscode.DebugConfiguration | undefined> {
        try {
            if (!existsSync(project))
                throw new Error(`C~ project does not exist: ${project}`);
            const launch = this.projects.readCompilerLaunch();
            const launchError = compilerPathError(launch, existsSync);
            if (launchError !== undefined)
                throw new Error(launchError);
            const target = this.projects.readProjectTarget(project);
            if (target === 'unknown')
                throw new Error(`Could not determine the C~ target in ${project}.`);

            const projectResource = vscode.Uri.file(project);
            const debuggerSettings = vscode.workspace.getConfiguration('ctilde.debugger', projectResource);
            const compilerSettings = vscode.workspace.getConfiguration('ctilde.compiler', projectResource);
            const serialPort = stringSetting(supplied.serialPort, debuggerSettings.get<string>('serialPort', ''));
            const baudRate = positiveNumber(supplied.baudRate, debuggerSettings.get<number>('baudRate', 115200));
            if (target === 'esp-idf' && serialPort.length === 0)
                throw new Error('ESP-IDF debugging requires ctilde.debugger.serialPort or serialPort in launch.json.');

            const descriptorDirectory = path.join(this.context.globalStorageUri.fsPath, 'debug-targets');
            mkdirSync(descriptorDirectory, { recursive: true });
            const descriptorName = createHash('sha256').update(path.resolve(project)).digest('hex').slice(0, 16) + '.json';
            const descriptor = path.join(descriptorDirectory, descriptorName);
            const args = [...launch.prefixArguments, '--project', project, '--prepare-debug', request, '--debug-target', descriptor];
            if (target === 'hosted') {
                const compiler = compilerSettings.get<string>('nativeCompiler', '').trim();
                if (request === 'launch' && compiler.length !== 0)
                    args.push('--compiler', compiler);
            } else {
                const idfPath = compilerSettings.get<string>('idfPath', '').trim();
                if (idfPath.length !== 0)
                    args.push('--idf-path', idfPath);
                args.push('--serial-port', serialPort, '--baud-rate', String(baudRate));
            }
            if (!await this.executePreparation(project, launch.command, args))
                return undefined;

            const prepared = JSON.parse(readFileSync(descriptor, 'utf8')) as PreparedDebugTarget;
            if (prepared.backend === 'msvc') {
                if (!allowMsvcFallback)
                    throw new Error('Manual type: ctilde configurations require a GDB-capable build. Select GCC or Clang, or use C~: Debug Project for MSVC fallback.');
                if (vscode.extensions.getExtension('ms-vscode.cpptools') === undefined)
                    throw new Error('MSVC debugging requires the Microsoft C/C++ extension (ms-vscode.cpptools).');
                const result: vscode.DebugConfiguration = {
                    type: 'cppvsdbg', request, name: supplied.name ?? 'Debug C~ Project',
                    program: prepared.program,
                    cwd: stringSetting(supplied.cwd, prepared.workingDirectory),
                    args: supplied.args ?? [],
                    stopAtEntry: supplied.stopAtEntry ?? false,
                    sourceFileMap: { '.': prepared.sourceRoot },
                };
                if (request === 'attach')
                    result.processId = supplied.processId ?? '${command:pickProcess}';
                return result;
            }

            return {
                ...supplied,
                type: 'ctilde', request,
                name: supplied.name ?? (request === 'launch' ? 'Debug C~ Project' : 'Attach C~ Debugger'),
                debugTarget: descriptor,
                gdbPath: stringSetting(supplied.gdbPath, debuggerSettings.get<string>('gdbPath', '')),
                serialPort,
                baudRate,
                showRuntimeFrames: supplied.showRuntimeFrames ?? debuggerSettings.get<boolean>('showRuntimeFrames', false),
                cwd: stringSetting(supplied.cwd, prepared.workingDirectory),
                processId: request === 'attach' && target === 'hosted'
                    ? supplied.processId ?? '${command:pickProcess}' : supplied.processId,
            };
        } catch (error) {
            void vscode.window.showErrorMessage(String(error instanceof Error ? error.message : error));
            return undefined;
        }
    }

    private async executePreparation(project: string, command: string, args: string[]): Promise<boolean> {
        const definition: vscode.TaskDefinition = { type: 'ctilde-debug-prepare', project };
        const execution = new vscode.ProcessExecution(command, args, { cwd: path.dirname(project) });
        const task = new vscode.Task(definition, vscode.TaskScope.Workspace, 'Prepare C~ Debugging', 'C~', execution,
            ['$ctilde', '$gcc', '$msCompile']);
        task.presentationOptions = { reveal: vscode.TaskRevealKind.Always, clear: true };
        const running = await vscode.tasks.executeTask(task);
        return new Promise(resolve => {
            const subscription = vscode.tasks.onDidEndTaskProcess(event => {
                if (event.execution !== running)
                    return;
                subscription.dispose();
                if (event.exitCode !== 0)
                    void vscode.window.showErrorMessage(`C~ debug preparation failed with exit code ${event.exitCode ?? 'unknown'}.`);
                resolve(event.exitCode === 0);
            });
        });
    }
}

function stringSetting(value: unknown, fallback: string): string {
    return typeof value === 'string' && value.trim().length !== 0 ? value.trim() : fallback.trim();
}

function positiveNumber(value: unknown, fallback: number): number {
    return typeof value === 'number' && Number.isInteger(value) && value > 0 ? value : fallback;
}

export async function deactivate(): Promise<void> {
    const current = controller;
    controller = undefined;
    await current?.shutdown();
}

class LanguageServerController {
    private readonly sourceWatchers: vscode.FileSystemWatcher[];
    private readonly restartCoordinator: RestartCoordinator;
    private readonly developmentWatchers: DevelopmentServerWatchManager;
    private client: LanguageClient | undefined;
    private launch: ServerLaunchConfiguration | undefined;
    private shadowDirectory: string | undefined;
    private shuttingDown = false;

    public constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly provider: StandardLibraryContentProvider,
    ) {
        this.sourceWatchers = [
            vscode.workspace.createFileSystemWatcher('**/*.ct'),
            vscode.workspace.createFileSystemWatcher('**/ctilde.json'),
        ];
        context.subscriptions.push(...this.sourceWatchers);
        this.restartCoordinator = new RestartCoordinator(
            () => this.restartNow(),
            750,
            error => this.reportUnexpectedRestartError(error),
        );
        this.developmentWatchers = new DevelopmentServerWatchManager(
            (directory, fileName) => vscode.workspace.createFileSystemWatcher(
                new vscode.RelativePattern(directory, fileName)),
            this.restartCoordinator,
        );
    }

    public start(): Promise<void> {
        return this.restartCoordinator.run();
    }

    public restart(): Promise<void> {
        this.restartCoordinator.cancelScheduled();
        return this.restartCoordinator.run();
    }

    public showOutput(): void {
        this.client?.outputChannel.show(true);
    }

    public configurationChanged(event: vscode.ConfigurationChangeEvent): void {
        if (event.affectsConfiguration('ctilde.languageServer.serverPath')
            || event.affectsConfiguration('ctilde.languageServer.dotnetPath')) {
            void this.restart().catch(error => this.reportUnexpectedRestartError(error));
            return;
        }
        if (event.affectsConfiguration('ctilde.languageServer.restartOnServerChange'))
            this.configureDevelopmentWatchers(this.launch);
    }

    public async shutdown(): Promise<void> {
        this.shuttingDown = true;
        this.developmentWatchers.dispose();
        await this.restartCoordinator.dispose();
        await this.stopClient();
        this.removeShadowDirectory();
    }

    private async restartNow(): Promise<void> {
        if (this.shuttingDown)
            return;

        let launch: ServerLaunchConfiguration;
        try {
            launch = this.readLaunchConfiguration();
        } catch (error) {
            this.launch = undefined;
            this.developmentWatchers.configure(undefined, false);
            void vscode.window.showErrorMessage(String(error instanceof Error ? error.message : error));
            return;
        }

        this.launch = launch;
        this.configureDevelopmentWatchers(launch);
        const pathError = serverPathError(launch, existsSync);
        if (pathError !== undefined) {
            void vscode.window.showErrorMessage(pathError);
            return;
        }

        await this.stopClient();
        this.removeShadowDirectory();
        let processLaunch = launch;
        if (launch.isExternal) {
            try {
                const staged = stageExternalServer(
                    launch,
                    path.join(this.context.globalStorageUri.fsPath, 'development-server'),
                );
                processLaunch = staged.launch;
                this.shadowDirectory = staged.shadowDirectory;
            } catch (error) {
                void vscode.window.showErrorMessage(
                    `C~ development language server could not be staged: ${String(error)}`);
                return;
            }
        }

        const languageClient = this.createClient(processLaunch);
        this.client = languageClient;
        this.provider.attach(languageClient);
        languageClient.outputChannel.appendLine(launch.isExternal
            ? `Using development language server: ${launch.serverDll}`
            : `Using bundled language server: ${launch.serverDll}`);
        try {
            await languageClient.start();
        } catch (error) {
            languageClient.outputChannel.appendLine(`Failed to start C~ language server: ${String(error)}`);
            void vscode.window.showErrorMessage(
                'C~ language server could not start. Check the configured server path and .NET 10 host.');
            if (this.client === languageClient) {
                this.client = undefined;
                this.provider.attach(undefined);
            }
            await languageClient.dispose();
        }
    }

    private readLaunchConfiguration(): ServerLaunchConfiguration {
        const configuration = vscode.workspace.getConfiguration('ctilde.languageServer');
        const configuredPath = configuration.get<string>('serverPath', '');
        const workspaceFolderPath = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
        return resolveServerLaunch(configuredPath, this.context.extensionPath, workspaceFolderPath);
    }

    private configureDevelopmentWatchers(launch: ServerLaunchConfiguration | undefined): void {
        const enabled = vscode.workspace.getConfiguration('ctilde.languageServer')
            .get<boolean>('restartOnServerChange', true);
        this.developmentWatchers.configure(
            launch?.isExternal === true ? launch.serverDll : undefined,
            enabled,
        );
    }

    private createClient(launch: ServerLaunchConfiguration): LanguageClient {
        const configuration = vscode.workspace.getConfiguration('ctilde.languageServer');
        const dotnetPath = configuration.get<string>('dotnetPath', 'dotnet');
        const serverOptions: ServerOptions = {
            command: dotnetPath,
            args: [launch.serverDll, '--stdio'],
            options: { cwd: launch.workingDirectory },
        };
        const clientOptions: LanguageClientOptions = {
            documentSelector: [
                { scheme: 'file', language: 'ctilde' },
            ],
            synchronize: {
                fileEvents: this.sourceWatchers,
            },
            markdown: { isTrusted: false, supportHtml: false },
        };
        return new LanguageClient('ctilde', 'C~ Language Server', serverOptions, clientOptions);
    }

    private async stopClient(): Promise<void> {
        const current = this.client;
        this.client = undefined;
        this.provider.attach(undefined);
        if (current !== undefined)
            await current.dispose();
    }

    private removeShadowDirectory(): void {
        const directory = this.shadowDirectory;
        this.shadowDirectory = undefined;
        if (directory === undefined)
            return;
        try {
            rmSync(directory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
        } catch {
            setTimeout(() => {
                try {
                    rmSync(directory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
                } catch {
                    // A stopped Windows process can retain a transient DLL mapping. A later
                    // extension-storage cleanup may remove this obsolete shadow directory.
                }
            }, 1000);
        }
    }

    private reportUnexpectedRestartError(error: unknown): void {
        const message = `C~ language server restart failed: ${String(error)}`;
        this.client?.outputChannel.appendLine(message);
        void vscode.window.showErrorMessage(message);
    }
}

class StandardLibraryContentProvider implements vscode.TextDocumentContentProvider {
    private languageClient: LanguageClient | undefined;

    public attach(languageClient: LanguageClient | undefined): void {
        this.languageClient = languageClient;
    }

    public async provideTextDocumentContent(uri: vscode.Uri): Promise<string> {
        if (this.languageClient === undefined) {
            return '// C~ language server is not running.\n';
        }
        const value = await this.languageClient.sendRequest<string | null>('ctilde/standardLibraryText', { uri: uri.toString() });
        return value ?? '// Standard-library source is unavailable.\n';
    }
}
