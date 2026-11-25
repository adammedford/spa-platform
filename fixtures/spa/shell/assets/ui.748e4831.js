// The shell's shared module (#998), reached by the bare name "fixture-ui" through the one import
// map in index.html, so a deployed run exercises the map: if the browser refuses the map, the
// shell's module cannot resolve this name and no fragment mounts. Named for its bytes, as every
// assets/ file is.
export function element(tag, text, attributes) {
  var node = document.createElement(tag);
  if (text !== undefined && text !== null) {
    node.textContent = text;
  }
  Object.keys(attributes || {}).forEach(function (name) {
    node.setAttribute(name, attributes[name]);
  });
  return node;
}
