import { cp, mkdir, mkdtemp, rm } from 'fs/promises';
import * as os from 'os';
import * as path from 'path';
import { runTests } from '@vscode/test-electron';

async function main(): Promise<void> {
    const extensionDevelopmentPath = path.resolve(__dirname, '..', '..');
    const extensionTestsPath = path.resolve(__dirname, 'suite', 'index.js');
    const mode = process.argv[2] ?? 'bundled';
    if (mode !== 'bundled' && mode !== 'external')
        throw new Error(`Unknown extension test mode: ${mode}. Expected bundled or external.`);
    const vscodeVersion = process.argv[3];
    const testRoot = await mkdtemp(path.join(os.tmpdir(), 'ctilde-vscode-test-'));
    const externalServerDirectory = path.join(testRoot, 'server');
    const workspaceDirectory = path.join(testRoot, 'workspace');
    if (mode === 'external')
        await cp(path.join(extensionDevelopmentPath, 'server'), externalServerDirectory, { recursive: true });
    await mkdir(workspaceDirectory);
    try {
        const extensionTestsEnv: Record<string, string> = {
            CTILDE_TEST_MODE: mode,
            CTILDE_TEST_WORKSPACE: workspaceDirectory,
        };
        if (mode === 'external') {
            extensionTestsEnv.CTILDE_TEST_EXTERNAL_SERVER = path.join(externalServerDirectory, 'CTilde.LanguageServer.dll');
            extensionTestsEnv.CTILDE_TEST_EXTERNAL_COMPILER = path.join(extensionDevelopmentPath, 'compiler', 'ctilde.dll');
        }
        await runTests({
            version: vscodeVersion,
            extensionDevelopmentPath,
            extensionTestsPath,
            launchArgs: [workspaceDirectory, '--disable-extensions', '--skip-welcome', '--skip-release-notes'],
            extensionTestsEnv,
        });
    } finally {
        await rm(testRoot, { recursive: true, force: true });
    }
}

main().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
