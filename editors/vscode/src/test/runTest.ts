import { cp, mkdir, mkdtemp, rm } from 'fs/promises';
import * as os from 'os';
import * as path from 'path';
import { runTests } from '@vscode/test-electron';

async function main(): Promise<void> {
    const extensionDevelopmentPath = path.resolve(__dirname, '..', '..');
    const extensionTestsPath = path.resolve(__dirname, 'suite', 'index.js');
    const externalServerRoot = await mkdtemp(path.join(os.tmpdir(), 'ctilde-vscode-server-'));
    const externalServerDirectory = path.join(externalServerRoot, 'server');
    const workspaceDirectory = path.join(externalServerRoot, 'workspace');
    await cp(path.join(extensionDevelopmentPath, 'server'), externalServerDirectory, { recursive: true });
    await mkdir(workspaceDirectory);
    try {
        await runTests({
            extensionDevelopmentPath,
            extensionTestsPath,
            launchArgs: [workspaceDirectory, '--disable-extensions', '--skip-welcome', '--skip-release-notes'],
            extensionTestsEnv: {
                CTILDE_TEST_EXTERNAL_SERVER: path.join(externalServerDirectory, 'CTilde.LanguageServer.dll'),
                CTILDE_TEST_EXTERNAL_COMPILER: path.join(extensionDevelopmentPath, 'compiler', 'ctilde.dll'),
                CTILDE_TEST_WORKSPACE: workspaceDirectory,
            },
        });
    } finally {
        await rm(externalServerRoot, { recursive: true, force: true });
    }
}

main().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
