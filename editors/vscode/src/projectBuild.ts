import * as path from 'path';

export type CTildeTaskMode = 'check' | 'build' | 'bindings';
export type CTildeProjectTarget = 'hosted' | 'esp-idf' | 'freestanding' | 'cosmopolitan' | 'unknown';

export interface CompilerLaunchConfiguration {
    readonly command: string;
    readonly prefixArguments: readonly string[];
    readonly workingDirectory: string;
    readonly compilerPath: string;
    readonly isExternal: boolean;
}

export interface BuildArgumentSettings {
    readonly nativeCompiler: string;
    readonly idfPath: string;
    readonly espClangPath: string;
}

export function resolveCompilerLaunch(
    configuredPath: string,
    dotnetPath: string,
    extensionPath: string,
    workspaceFolderPath: string | undefined,
): CompilerLaunchConfiguration {
    const value = configuredPath.trim();
    if (value.length === 0) {
        const compilerPath = path.join(extensionPath, 'compiler', 'ctilde.dll');
        return {
            command: dotnetPath,
            prefixArguments: [compilerPath],
            workingDirectory: extensionPath,
            compilerPath,
            isExternal: false,
        };
    }
    if (workspaceFolderPath === undefined && value.includes('${workspaceFolder}'))
        throw new Error('ctilde.compiler.compilerPath uses ${workspaceFolder}, but no workspace folder is open.');
    const expanded = workspaceFolderPath === undefined
        ? value
        : value.replaceAll('${workspaceFolder}', workspaceFolderPath);
    if (!path.isAbsolute(expanded) && workspaceFolderPath === undefined)
        throw new Error('A relative ctilde.compiler.compilerPath requires an open workspace folder.');
    const compilerPath = path.normalize(path.isAbsolute(expanded)
        ? expanded
        : path.resolve(workspaceFolderPath!, expanded));
    const isDll = path.extname(compilerPath).toLocaleLowerCase() === '.dll';
    return {
        command: isDll ? dotnetPath : compilerPath,
        prefixArguments: isDll ? [compilerPath] : [],
        workingDirectory: path.dirname(compilerPath),
        compilerPath,
        isExternal: true,
    };
}

export function compilerPathError(
    launch: CompilerLaunchConfiguration,
    fileExists: (fileName: string) => boolean,
): string | undefined {
    if (fileExists(launch.compilerPath))
        return undefined;
    return launch.isExternal
        ? `Configured C~ compiler does not exist: ${launch.compilerPath}. Build it or update ctilde.compiler.compilerPath.`
        : `Bundled C~ compiler does not exist: ${launch.compilerPath}. Reinstall the extension.`;
}

export function compilerArguments(
    launch: CompilerLaunchConfiguration,
    manifestPath: string,
    mode: CTildeTaskMode,
    target: CTildeProjectTarget,
    settings: BuildArgumentSettings,
): string[] {
    const result = [...launch.prefixArguments, '--project', manifestPath,
        mode === 'build' ? '--build' : mode === 'bindings' ? '--generate-bindings' : '--check'];
    if (mode === 'build' && (target === 'hosted' || target === 'cosmopolitan') && settings.nativeCompiler.trim().length !== 0)
        result.push('--compiler', settings.nativeCompiler.trim());
    if (target === 'esp-idf' && settings.idfPath.trim().length !== 0)
        result.push('--idf-path', settings.idfPath.trim());
    if (target === 'esp-idf' && settings.espClangPath.trim().length !== 0)
        result.push('--esp-clang', settings.espClangPath.trim());
    return result;
}

export function resolveTaskProjectPath(project: string, workspaceFolderPath: string | undefined): string {
    const value = project.trim();
    if (value.length === 0)
        throw new Error('A C~ task requires a project path.');
    if (path.isAbsolute(value))
        return path.normalize(value);
    if (workspaceFolderPath === undefined)
        throw new Error('A relative C~ task project path requires a workspace folder.');
    return path.resolve(workspaceFolderPath, value);
}

export function resolveDebugProjectPath(project: string, workspaceFolderPath: string | undefined): string {
    const value = project.trim();
    if (value.length === 0)
        throw new Error('A C~ debug configuration requires a project path.');
    if (workspaceFolderPath === undefined && value.includes('${workspaceFolder}'))
        throw new Error('The C~ debug project uses ${workspaceFolder}, but no workspace folder is open.');
    const expanded = workspaceFolderPath === undefined
        ? value
        : value.replaceAll('${workspaceFolder}', workspaceFolderPath);
    if (path.isAbsolute(expanded))
        return path.normalize(expanded);
    if (workspaceFolderPath === undefined)
        throw new Error('A relative C~ debug project path requires a workspace folder.');
    return path.resolve(workspaceFolderPath, expanded);
}

export function findNearestProject(sourcePath: string, fileExists: (fileName: string) => boolean): string | undefined {
    let directory = path.dirname(path.resolve(sourcePath));
    while (true) {
        const candidate = path.join(directory, 'ctilde.json');
        if (fileExists(candidate))
            return candidate;
        const parent = path.dirname(directory);
        if (parent === directory)
            return undefined;
        directory = parent;
    }
}
