import {
    cpSync,
    mkdirSync,
    mkdtempSync,
    readdirSync,
    rmSync,
} from 'fs';
import * as path from 'path';

export interface ServerLaunchConfiguration {
    readonly serverDll: string;
    readonly workingDirectory: string;
    readonly isExternal: boolean;
}

export interface StagedServerLaunch {
    readonly launch: ServerLaunchConfiguration;
    readonly shadowDirectory: string;
}

export interface DisposableLike {
    dispose(): void;
}

export interface WatchedFileLike extends DisposableLike {
    onDidCreate(listener: () => void): DisposableLike;
    onDidChange(listener: () => void): DisposableLike;
}

export type WatchedFileFactory = (directory: string, fileName: string) => WatchedFileLike;

export function resolveServerLaunch(
    configuredPath: string,
    extensionPath: string,
    workspaceFolderPath: string | undefined,
): ServerLaunchConfiguration {
    const value = configuredPath.trim();
    if (value.length === 0) {
        return {
            serverDll: path.join(extensionPath, 'server', 'CTilde.LanguageServer.dll'),
            workingDirectory: extensionPath,
            isExternal: false,
        };
    }

    if (workspaceFolderPath === undefined && value.includes('${workspaceFolder}')) {
        throw new Error('ctilde.languageServer.serverPath uses ${workspaceFolder}, but no workspace folder is open.');
    }

    const expanded = workspaceFolderPath === undefined
        ? value
        : value.replaceAll('${workspaceFolder}', workspaceFolderPath);
    if (!path.isAbsolute(expanded) && workspaceFolderPath === undefined) {
        throw new Error('A relative ctilde.languageServer.serverPath requires an open workspace folder.');
    }

    const serverDll = path.normalize(path.isAbsolute(expanded)
        ? expanded
        : path.resolve(workspaceFolderPath!, expanded));
    return {
        serverDll,
        workingDirectory: path.dirname(serverDll),
        isExternal: true,
    };
}

export function serverPathError(
    launch: ServerLaunchConfiguration,
    fileExists: (fileName: string) => boolean,
): string | undefined {
    if (fileExists(launch.serverDll))
        return undefined;
    if (launch.isExternal) {
        return `Configured C~ language server does not exist: ${launch.serverDll}. `
            + 'Build CTilde.LanguageServer or update ctilde.languageServer.serverPath.';
    }
    return `Bundled C~ language server does not exist: ${launch.serverDll}. Reinstall the extension.`;
}

export function stageExternalServer(
    launch: ServerLaunchConfiguration,
    storageRoot: string,
): StagedServerLaunch {
    if (!launch.isExternal)
        throw new Error('Only an external language server can be staged.');

    mkdirSync(storageRoot, { recursive: true });
    const shadowDirectory = mkdtempSync(path.join(storageRoot, 'server-'));
    const sourceDirectory = path.dirname(launch.serverDll);
    try {
        for (const entry of readdirSync(sourceDirectory)) {
            cpSync(
                path.join(sourceDirectory, entry),
                path.join(shadowDirectory, entry),
                { recursive: true },
            );
        }
    } catch (error) {
        rmSync(shadowDirectory, { recursive: true, force: true });
        throw error;
    }

    return {
        launch: {
            serverDll: path.join(shadowDirectory, path.basename(launch.serverDll)),
            workingDirectory: launch.workingDirectory,
            isExternal: true,
        },
        shadowDirectory,
    };
}

export class RestartCoordinator {
    private queue: Promise<void> = Promise.resolve();
    private timer: ReturnType<typeof setTimeout> | undefined;
    private disposed = false;

    public constructor(
        private readonly action: () => Promise<void>,
        private readonly delayMilliseconds = 750,
        private readonly reportError: (error: unknown) => void = () => undefined,
    ) {
    }

    public run(): Promise<void> {
        if (this.disposed)
            return Promise.resolve();
        const operation = this.queue.then(async () => {
            if (!this.disposed)
                await this.action();
        });
        this.queue = operation.catch(() => undefined);
        return operation;
    }

    public schedule(): void {
        if (this.disposed)
            return;
        this.cancelScheduled();
        this.timer = setTimeout(() => {
            this.timer = undefined;
            void this.run().catch(this.reportError);
        }, this.delayMilliseconds);
    }

    public cancelScheduled(): void {
        if (this.timer !== undefined) {
            clearTimeout(this.timer);
            this.timer = undefined;
        }
    }

    public async dispose(): Promise<void> {
        this.disposed = true;
        this.cancelScheduled();
        await this.queue;
    }
}

export class DevelopmentServerWatchManager implements DisposableLike {
    private disposables: DisposableLike[] = [];

    public constructor(
        private readonly createWatcher: WatchedFileFactory,
        private readonly restart: RestartCoordinator,
    ) {
    }

    public configure(serverDll: string | undefined, enabled: boolean): void {
        this.restart.cancelScheduled();
        this.disposeWatchers();
        if (!enabled || serverDll === undefined)
            return;

        const directory = path.dirname(serverDll);
        const names = new Map<string, string>();
        for (const fileName of [path.basename(serverDll), 'CTilde.Compiler.dll'])
            names.set(fileName.toLocaleLowerCase(), fileName);

        for (const fileName of names.values()) {
            const watcher = this.createWatcher(directory, fileName);
            this.disposables.push(
                watcher,
                watcher.onDidCreate(() => this.restart.schedule()),
                watcher.onDidChange(() => this.restart.schedule()),
            );
        }
    }

    public dispose(): void {
        this.restart.cancelScheduled();
        this.disposeWatchers();
    }

    private disposeWatchers(): void {
        for (const disposable of this.disposables.splice(0))
            disposable.dispose();
    }
}
