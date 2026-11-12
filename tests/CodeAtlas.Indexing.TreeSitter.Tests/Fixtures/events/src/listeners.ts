import { bus } from "./bus";

export function sendMail(id: number): void {
  console.log("mail", id);
}

export function updateStats(id: number): void {
  console.log("stats", id);
}

export function logError(e: Error): void {
  console.log(e);
}

export class Auditor {
  start(): void {
    bus.once("order:audited", this.onAudited);
  }

  onAudited(id: number): void {
    console.log("audited", id);
  }
}

export function register(): void {
  bus.on("order:placed", sendMail);
  bus.addListener("order:placed", updateStats);
  bus.on("error", logError);
  bus.on("order:placed", (id: number) => console.log(id));
  bus.off("order:placed", sendMail);
}
