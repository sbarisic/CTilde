import * as assert from 'assert';
import { access, mkdir, writeFile } from 'fs/promises';
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
    const externalServer = process.env.CTILDE_TEST_EXTERNAL_SERVER;
    assert.ok(externalServer, 'External C~ test language server was not configured.');
    const externalCompiler = process.env.CTILDE_TEST_EXTERNAL_COMPILER;
    assert.ok(externalCompiler, 'External C~ test compiler was not configured.');
    const directory = process.env.CTILDE_TEST_WORKSPACE;
    assert.ok(directory, 'C~ extension test workspace was not configured.');
    const languageServerConfiguration = vscode.workspace.getConfiguration('ctilde.languageServer');
    await languageServerConfiguration.update('serverPath', externalServer, vscode.ConfigurationTarget.Global);
    await languageServerConfiguration.update('restartOnServerChange', true, vscode.ConfigurationTarget.Global);
    const compilerConfiguration = vscode.workspace.getConfiguration('ctilde.compiler');
    await compilerConfiguration.update('compilerPath', externalCompiler, vscode.ConfigurationTarget.Global);
    await compilerConfiguration.update('nativeCompiler', '', vscode.ConfigurationTarget.Global);
    await extension.activate();
    const filePath = path.join(directory, 'Program.ct');
    const source = `// TextMate fallback
using System;
/// <summary>Provides documented overloads.</summary>
public static class Docs
{
    /// <summary>Adds two integers.</summary>
    /// <param name="left">The left integer.</param>
    /// <param name="right">The right integer.</param>
    /// <returns>The integer sum.</returns>
    public static int Add(int left, int right) { return left + right; }
    public static uint Add(uint left, uint right) { return left + right; }
}
public static class Program { [EntryPoint] public static void Main() { string text = "hello"; int sum = Docs.Add(1, 2); Console. } }`;
    await writeFile(path.join(directory, 'ctilde.json'), JSON.stringify({ target: 'hosted', sources: ['*.ct'] }));
    await writeFile(filePath, source);
    try {
        const document = await vscode.workspace.openTextDocument(filePath);
        assert.equal(document.languageId, 'ctilde');
        const editor = await vscode.window.showTextDocument(document);
        const completionPosition = document.positionAt(source.indexOf('Console.') + 'Console.'.length);
        const completions = await waitFor(
            async () => vscode.commands.executeCommand<vscode.CompletionList>('vscode.executeCompletionItemProvider', document.uri, completionPosition, '.', 100),
            value => value.items.some(item => item.label === 'WriteLine' && documentationText(item.documentation).includes('line terminator')),
            value => value.items.slice(0, 20).map(item => typeof item.label === 'string' ? item.label : item.label.label).join(', '));
        assert.ok(completions.items.some(item => item.label === 'WriteLine'));
        const documentedCompletion = completions.items.find(item => item.label === 'WriteLine' && documentationText(item.documentation).includes('line terminator'));
        assert.ok(documentedCompletion, 'Resolved Console.WriteLine completion documentation was missing.');

        const addPosition = document.positionAt(source.indexOf('Docs.Add(1') + 'Docs.'.length + 1);
        const hovers = await vscode.commands.executeCommand<vscode.Hover[]>('vscode.executeHoverProvider', document.uri, addPosition);
        assert.ok(hovers.some(hover => hover.contents.some(content => documentationText(content).includes('Adds two integers.'))));
        const signaturePosition = document.positionAt(source.indexOf(', 2') + 2);
        const signature = await vscode.commands.executeCommand<vscode.SignatureHelp>('vscode.executeSignatureHelpProvider', document.uri, signaturePosition, ',');
        assert.ok(signature?.signatures.some(item => item.parameters[1] !== undefined && documentationText(item.parameters[1].documentation).includes('right integer')));

        await vscode.commands.executeCommand('ctilde.languageServer.restart');
        const restartedCompletions = await waitFor(
            async () => vscode.commands.executeCommand<vscode.CompletionList>('vscode.executeCompletionItemProvider', document.uri, completionPosition, '.'),
            value => value.items.some(item => item.label === 'WriteLine'),
            value => value.items.slice(0, 20).map(item => typeof item.label === 'string' ? item.label : item.label.label).join(', '));
        assert.ok(restartedCompletions.items.some(item => item.label === 'WriteLine'));

        const definitionPosition = document.positionAt(source.indexOf('Console') + 1);
        const definitions = await vscode.commands.executeCommand<Array<vscode.Location | vscode.LocationLink>>(
            'vscode.executeDefinitionProvider', document.uri, definitionPosition);
        assert.ok(definitions.some(definition => ('uri' in definition ? definition.uri : definition.targetUri).scheme === 'ctilde-stdlib'));

        const semantic = await waitFor(
            async () => semanticTokens(document),
            value => value.some(token => token.text === 'Console' && token.type === 'class'),
            value => value.map(token => `${token.text}:${token.type}`).join(', '));
        assert.ok(semantic.some(token => token.text === 'Program' && token.type === 'class' && token.modifiers.includes('declaration')));
        assert.ok(semantic.some(token => token.text === 'Main' && token.type === 'method' && token.modifiers.includes('static')));
        assert.ok(semantic.some(token => token.text === 'Console' && token.type === 'class' && token.modifiers.includes('defaultLibrary')));
        assert.ok(!semantic.some(token => token.text === 'public' || token.text === '"hello"' || token.text.includes('TextMate')));

        await editor.edit(builder => builder.insert(new vscode.Position(0, 0), 'public class UnsavedMarker { }\n'));
        const symbols = await waitFor(
            async () => vscode.commands.executeCommand<vscode.SymbolInformation[]>('vscode.executeWorkspaceSymbolProvider', 'UnsavedMarker'),
            value => value.some(symbol => symbol.name === 'UnsavedMarker'),
            value => value.map(symbol => symbol.name).join(', '));
        assert.ok(symbols.some(symbol => symbol.name === 'UnsavedMarker'));
        const changedSemantic = await waitFor(
            async () => semanticTokens(document),
            value => value.some(token => token.text === 'UnsavedMarker' && token.type === 'class'),
            value => value.map(token => `${token.text}:${token.type}`).join(', '));
        assert.ok(changedSemantic.some(token => token.text === 'UnsavedMarker' && token.modifiers.includes('declaration')));

        const hostedIo = await hostedIoFeatures(directory);
        assert.ok(hostedIo.labels.includes('Open'));
        assert.ok(hostedIo.documentation.includes('Opens, creates, or appends'));

        const hosted = await targetFeatures(directory, 'hosted');
        const esp = await targetFeatures(directory, 'esp-idf');
        assert.ok(!hosted.labels.includes('Ws2812'));
        assert.ok(esp.labels.includes('Ws2812'));
        assert.ok(!hosted.semantic.some(token => token.text === 'Ws2812'));
        assert.ok(esp.semantic.some(token => token.text === 'Ws2812' && token.type === 'class' && token.modifiers.includes('defaultLibrary')));

        await nativeBuildFeatures(directory);
    } finally {
        await vscode.commands.executeCommand('workbench.action.closeAllEditors');
        await new Promise(resolve => setTimeout(resolve, 200));
    }
}

async function nativeBuildFeatures(root: string): Promise<void> {
    const directory = path.join(root, 'native-build');
    const filePath = path.join(directory, 'Program.ct');
    const source = 'public static class Program { [EntryPoint] public static void Main() { } }';
    await mkdir(directory);
    await writeFile(path.join(directory, 'ctilde.json'), JSON.stringify({ target: 'hosted', sources: ['Program.ct'] }));
    await writeFile(filePath, source);
    const document = await vscode.workspace.openTextDocument(filePath);
    await vscode.window.showTextDocument(document);
    await vscode.commands.executeCommand('ctilde.project.build');
    const generated = path.join(directory, 'build', 'generated', 'ctilde_program.c');
    const executable = path.join(directory, 'build', `native-build${process.platform === 'win32' ? '.exe' : ''}`);
    await waitForFiles([generated, executable]);
}

async function waitForFiles(files: string[]): Promise<void> {
    const deadline = Date.now() + 30000;
    while (Date.now() < deadline) {
        const present = await Promise.all(files.map(async file => {
            try { await access(file); return true; } catch { return false; }
        }));
        if (present.every(Boolean))
            return;
        await new Promise(resolve => setTimeout(resolve, 200));
    }
    throw new Error(`Timed out waiting for C~ build outputs: ${files.join(', ')}`);
}

async function hostedIoFeatures(root: string): Promise<{ labels: string[]; documentation: string }> {
    const directory = path.join(root, 'hosted-io');
    const filePath = path.join(directory, 'Program.ct');
    const source = 'using System.IO; public static class Program { [EntryPoint] public static void Main() { File. } }';
    await mkdir(directory);
    await writeFile(path.join(directory, 'ctilde.json'), JSON.stringify({ target: 'hosted', sources: ['Program.ct'] }));
    await writeFile(filePath, source);
    const document = await vscode.workspace.openTextDocument(filePath);
    await vscode.window.showTextDocument(document);
    const position = document.positionAt(source.indexOf('File.') + 'File.'.length);
    const completions = await waitFor(
        async () => vscode.commands.executeCommand<vscode.CompletionList>('vscode.executeCompletionItemProvider', document.uri, position, '.', 100),
        value => value.items.some(item => item.label === 'Open'),
        value => value.items.slice(0, 20).map(item => typeof item.label === 'string' ? item.label : item.label.label).join(', '));
    const open = completions.items.find(item => item.label === 'Open');
    return {
        labels: completions.items.map(item => typeof item.label === 'string' ? item.label : item.label.label),
        documentation: documentationText(open?.documentation),
    };
}

async function targetFeatures(root: string, target: 'hosted' | 'esp-idf'): Promise<{ labels: string[]; semantic: DecodedSemanticToken[] }> {
    const directory = path.join(root, target);
    const filePath = path.join(directory, 'Program.ct');
    const source = 'using Esp.Idf; public static class Program { [EntryPoint] public static void Main() { Ws2812 } }';
    await mkdir(directory);
    await writeFile(path.join(directory, 'ctilde.json'), JSON.stringify({ target, sources: ['Program.ct'] }));
    await writeFile(filePath, source);
    const document = await vscode.workspace.openTextDocument(filePath);
    await vscode.window.showTextDocument(document);
    const position = document.positionAt(source.indexOf('Ws2812') + 'Ws2812'.length);
    const completions = await waitFor(
        async () => vscode.commands.executeCommand<vscode.CompletionList>('vscode.executeCompletionItemProvider', document.uri, position),
        value => target === 'hosted' || value.items.some(item => item.label === 'Ws2812'),
        value => value.items.slice(0, 20).map(item => typeof item.label === 'string' ? item.label : item.label.label).join(', '));
    const semantic = await waitFor(
        async () => semanticTokens(document),
        value => target === 'hosted' || value.some(token => token.text === 'Ws2812'),
        value => value.map(token => `${token.text}:${token.type}`).join(', '));
    return {
        labels: completions.items.map(item => typeof item.label === 'string' ? item.label : item.label.label),
        semantic,
    };
}

interface DecodedSemanticToken {
    text: string;
    type: string;
    modifiers: string[];
}

function documentationText(value: vscode.MarkdownString | vscode.MarkedString | string | undefined): string {
    if (value === undefined)
        return '';
    if (typeof value === 'string')
        return value;
    return value.value;
}

async function semanticTokens(document: vscode.TextDocument): Promise<DecodedSemanticToken[]> {
    const legend = await vscode.commands.executeCommand<vscode.SemanticTokensLegend>('vscode.provideDocumentSemanticTokensLegend', document.uri);
    const tokens = await vscode.commands.executeCommand<vscode.SemanticTokens>('vscode.provideDocumentSemanticTokens', document.uri);
    if (legend === undefined || tokens === undefined)
        return [];
    const result: DecodedSemanticToken[] = [];
    let line = 0;
    let character = 0;
    for (let index = 0; index < tokens.data.length; index += 5) {
        line += tokens.data[index];
        character = tokens.data[index] === 0 ? character + tokens.data[index + 1] : tokens.data[index + 1];
        const length = tokens.data[index + 2];
        const modifierBits = tokens.data[index + 4];
        result.push({
            text: document.getText(new vscode.Range(line, character, line, character + length)),
            type: legend.tokenTypes[tokens.data[index + 3]],
            modifiers: legend.tokenModifiers.filter((_, modifier) => (modifierBits & (1 << modifier)) !== 0),
        });
    }
    return result;
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
