// Fixture fragment order-history's entry module (#998): an ES module at the root of its release, at a
// name that is constant across releases and not hashed, which the shell loads with
// import(entryUrl) from GET /bff/fragments. Its chunk is imported by a relative URL, so it resolves
// under this fragment's assets/.
import { mount as panel } from "./assets/panel.d2ff9911.js";

export function mount(element) {
  panel(element, "order-history", "orders");
}
