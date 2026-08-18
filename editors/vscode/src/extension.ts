import * as path from 'path';
import * as vscode from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
} from 'vscode-languageclient/node';

let client: LanguageClient | undefined;
let watchers: vscode.FileSystemWatcher[] | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    const provider = new StandardLibraryContentProvider();
    context.subscriptions.push(vscode.workspace.registerTextDocumentContentProvider('ctilde-stdlib', provider));
    context.subscriptions.push(vscode.commands.registerCommand('ctilde.languageServer.restart', async () => {
        await stopClient();
        client = createClient(context);
        provider.attach(client);
        await startClient(client);
    }));
    context.subscriptions.push(vscode.commands.registerCommand('ctilde.languageServer.showOutput', () => client?.outputChannel.show(true)));

    client = createClient(context);
    provider.attach(client);
    await startClient(client);
}

export async function deactivate(): Promise<void> {
    await stopClient();
}

function createClient(context: vscode.ExtensionContext): LanguageClient {
    const configuration = vscode.workspace.getConfiguration('ctilde.languageServer');
    const dotnetPath = configuration.get<string>('dotnetPath', 'dotnet');
    const serverDll = context.asAbsolutePath(path.join('server', 'CTilde.LanguageServer.dll'));
    const serverOptions: ServerOptions = {
        command: dotnetPath,
        args: [serverDll, '--stdio'],
        options: { cwd: context.extensionPath },
    };
    if (watchers === undefined) {
        watchers = [
            vscode.workspace.createFileSystemWatcher('**/*.ct'),
            vscode.workspace.createFileSystemWatcher('**/ctilde.json'),
        ];
        context.subscriptions.push(...watchers);
    }
    const clientOptions: LanguageClientOptions = {
        documentSelector: [
            { scheme: 'file', language: 'ctilde' },
        ],
        synchronize: {
            fileEvents: watchers,
        },
        markdown: { isTrusted: false, supportHtml: false },
    };
    return new LanguageClient('ctilde', 'C~ Language Server', serverOptions, clientOptions);
}

async function startClient(languageClient: LanguageClient): Promise<void> {
    try {
        await languageClient.start();
    } catch (error) {
        languageClient.outputChannel.appendLine(`Failed to start C~ language server: ${String(error)}`);
        void vscode.window.showErrorMessage('C~ language server could not start. Install the .NET 10 runtime or configure ctilde.languageServer.dotnetPath.');
        throw error;
    }
}

async function stopClient(): Promise<void> {
    const current = client;
    client = undefined;
    if (current !== undefined) {
        await current.stop();
    }
}

class StandardLibraryContentProvider implements vscode.TextDocumentContentProvider {
    private languageClient: LanguageClient | undefined;

    public attach(languageClient: LanguageClient): void {
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
