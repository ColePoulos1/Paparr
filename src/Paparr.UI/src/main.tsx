import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { Theme } from "@astryxdesign/core/theme";
import { matchaTheme } from "@astryxdesign/theme-matcha/built";
import App from "./App";
import AuthGate from "./components/AuthGate";
import "./index.css";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <Theme theme={matchaTheme} mode="system">
      <AuthGate>{({ signOut }) => <App signOut={signOut} />}</AuthGate>
    </Theme>
  </StrictMode>,
);
