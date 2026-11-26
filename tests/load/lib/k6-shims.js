// What @karry/crypto (crypto.gen.js) expects from a browser and k6 lacks: TextEncoder (UTF-8), btoa and atob. Import
// this module before using the bundle. Only what the bundle calls is provided.
import encoding from "k6/encoding";

if (typeof globalThis.TextEncoder === "undefined") {
  globalThis.TextEncoder = class {
    encode(text) {
      const bytes = [];
      for (const char of text) {
        let code = char.codePointAt(0);
        if (code < 0x80) {
          bytes.push(code);
        } else if (code < 0x800) {
          bytes.push(0xc0 | (code >> 6), 0x80 | (code & 0x3f));
        } else if (code < 0x10000) {
          bytes.push(0xe0 | (code >> 12), 0x80 | ((code >> 6) & 0x3f), 0x80 | (code & 0x3f));
        } else {
          bytes.push(0xf0 | (code >> 18), 0x80 | ((code >> 12) & 0x3f), 0x80 | ((code >> 6) & 0x3f), 0x80 | (code & 0x3f));
        }
      }

      return new Uint8Array(bytes);
    }
  };
}

/** Bytes of a "binary string" (one char per byte), as btoa takes and atob gives. */
function binaryBytes(binary) {
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i) & 0xff;
  return bytes;
}

if (typeof globalThis.btoa === "undefined") {
  globalThis.btoa = (binary) => encoding.b64encode(binaryBytes(binary).buffer, "std");
}

if (typeof globalThis.atob === "undefined") {
  globalThis.atob = (text) => {
    const bytes = new Uint8Array(encoding.b64decode(text, "std"));
    let binary = "";
    for (const byte of bytes) binary += String.fromCharCode(byte);
    return binary;
  };
}
