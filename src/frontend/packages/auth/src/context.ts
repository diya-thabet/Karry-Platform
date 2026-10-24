import { createContext } from "react";
import type { Auth } from "./auth";

export const AuthContext = createContext<Auth | null>(null);
