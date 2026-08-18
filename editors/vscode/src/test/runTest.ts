import * as path from 'path';
import { runTests } from '@vscode/test-electron';

async function main(): Promise<void> {
    const extensionDevelopmentPath = path.resolve(__dirname, '..', '..');
    const extensionTestsPath = path.resolve(__dirname, 'suite', 'index.js');
    await runTests({
        extensionDevelopmentPath,
        extensionTestsPath,
        launchArgs: ['--disable-extensions', '--skip-welcome', '--skip-release-notes'],
    });
}

main().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
