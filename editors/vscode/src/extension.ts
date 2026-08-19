import { existsSync, rmSync } from 'fs';
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

let controller: LanguageServerController | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    const provider = new StandardLibraryContentProvider();
    controller = new LanguageServerController(context, provider);
    context.subscriptions.push(
        vscode.workspace.registerTextDocumentContentProvider('ctilde-stdlib', provider),
        vscode.commands.registerCommand('ctilde.languageServer.restart', () => controller?.restart()),
        vscode.commands.registerCommand('ctilde.languageServer.showOutput', () => controller?.showOutput()),
        vscode.workspace.onDidChangeConfiguration(event => controller?.configurationChanged(event)),
    );
    await controller.start();
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
