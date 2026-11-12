import { EventEmitter } from "events";

export const bus = new EventEmitter();

export class OrderService {
  place(id: number): void {
    save(id);
    bus.emit("order:placed", id);
    bus.emit(`order:audited`, id);
    bus.emit("error", new Error("x"));
    bus.emit(channelOf(id), id);
  }
}

export function save(id: number): number {
  return id;
}

function channelOf(id: number): string {
  return "order:" + id;
}
