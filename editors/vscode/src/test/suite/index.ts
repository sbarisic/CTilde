import * as assert from 'assert';
import { mkdir, mkdtemp, rm, writeFile } from 'fs/promises';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';

export function run(): Promise<void> {
    return extensionSmokeTest().catch(error => {
        console.error('C~ extension-host smoke test failed:', error);
        throw error;
    });
}

async function extensionSmokeTest(): Promise<void> {
    const extension = vscode.extensions.getExtension('ctilde.ctilde-language');
    assert.ok(extension, 'C~ development extension was not discovered.');
    await extension.activate();
    const directory = await mkdtemp(path.join(os.tmpdir(), 'ctilde-vscode-'));
    const filePath = path.join(directory, 'Program.ct');
    const source = 'using System; public static class Program { [EntryPoint] public static void Main() { Console. } }';
    await writeFile(path.join(directory, 'ctilde.json'), JSON.stringify({ target: 'hosted', sources: ['*.ct'] }));
    await writeFile(filePath, source);
    try {
        const document = await vscode.workspace.openTextDocument(filePath);
        assert.equal(document.languageId, 'ctilde');
        const editor = await vscode.window.showTextDocument(document);
        const completionPosition = document.positionAt(source.indexOf('Console.') + 'Console.'.length);
        const completions = await waitFor(
            async () => vscode.commands.executeCommand<vscode.CompletionList>('vscode.executeCompletionItemProvider', document.uri, completionPosition, '.'),
            value => value.items.some(item => item.label === 'WriteLine'),
            value => value.items.slice(0, 20).map(item => typeof item.label === 'string' ? item.label : item.label.label).join(', '));
        assert.ok(completions.items.some(item => item.label === 'WriteLine'));

        const definitionPosition = document.positionAt(source.indexOf('Console') + 1);
        const definitions = await vscode.commands.executeCommand<Array<vscode.Location | vscode.LocationLink>>(
            'vscode.executeDefinitionProvider', document.uri, definitionPosition);
        assert.ok(definitions.some(definition => ('uri' in definition ? definition.uri : definition.targetUri).scheme === 'ctilde-stdlib'));

        await editor.edit(builder => builder.insert(new vscode.Position(0, 0), 'public class UnsavedMarker { }\n'));
        const symbols = await waitFor(
            async () => vscode.commands.executeCommand<vscode.SymbolInformation[]>('vscode.executeWorkspaceSymbolProvider', 'UnsavedMarker'),
            value => value.some(symbol => symbol.name === 'UnsavedMarker'),
            value => value.map(symbol => symbol.name).join(', '));
        assert.ok(symbols.some(symbol => symbol.name === 'UnsavedMarker'));

        const hostedLabels = await targetCompletionLabels(directory, 'hosted');
        const espLabels = await targetCompletionLabels(directory, 'esp-idf');
        assert.ok(!hostedLabels.includes('Ws2812'));
        assert.ok(espLabels.includes('Ws2812'));
    } finally {
        await vscode.commands.executeCommand('workbench.action.closeActiveEditor');
        await rm(directory, { recursive: true, force: true });
    }
}

async function targetCompletionLabels(root: string, target: 'hosted' | 'esp-idf'): Promise<string[]> {
    const directory = path.join(root, target);
    const filePath = path.join(directory, 'Program.ct');
    const source = 'using Esp.Idf; public static class Program { [EntryPoint] public static void Main() { Ws } }';
    await mkdir(directory);
    await writeFile(path.join(directory, 'ctilde.json'), JSON.stringify({ target, sources: ['Program.ct'] }));
    await writeFile(filePath, source);
    const document = await vscode.workspace.openTextDocument(filePath);
    await vscode.window.showTextDocument(document);
    const position = document.positionAt(source.indexOf('Ws }') + 2);
    const completions = await waitFor(
        async () => vscode.commands.executeCommand<vscode.CompletionList>('vscode.executeCompletionItemProvider', document.uri, position),
        value => target === 'hosted' || value.items.some(item => item.label === 'Ws2812'),
        value => value.items.slice(0, 20).map(item => typeof item.label === 'string' ? item.label : item.label.label).join(', '));
    return completions.items.map(item => typeof item.label === 'string' ? item.label : item.label.label);
}

async function waitFor<T>(action: () => Thenable<T | undefined>, isReady: (value: T) => boolean, describe: (value: T) => string): Promise<T> {
    const deadline = Date.now() + 10000;
    let last = '<undefined>';
    do {
        const value = await action();
        if (value !== undefined) {
            if (isReady(value))
                return value;
            last = describe(value);
        }
        await new Promise(resolve => setTimeout(resolve, 200));
    } while (Date.now() < deadline);
    throw new Error(`Timed out waiting for the C~ language server. Last completions: ${last}`);
}
