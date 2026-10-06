declare module 'node:crypto' {
  interface Hash {
    update(data: string | Uint8Array, inputEncoding?: string): Hash;
    digest(encoding: 'hex'): string;
  }
  export function createHash(algorithm: string): Hash;
}

declare module 'node:fs' {
  export function readFileSync(path: string): Uint8Array;
  export function readdirSync(path: string): string[];
}

declare module 'node:path' {
  export function dirname(path: string): string;
  export function resolve(...paths: string[]): string;
}

declare module 'node:url' {
  export function fileURLToPath(url: string | URL): string;
}

declare class Buffer extends Uint8Array {
  static from(value: string, encoding: 'utf8' | 'base64'): Buffer;
  toString(encoding?: string): string;
}
