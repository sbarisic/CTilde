(() => {
  "use strict";

  const tabList = document.querySelector('[role="tablist"]');

  if (!tabList) {
    return;
  }

  const tabs = Array.from(tabList.querySelectorAll('[role="tab"]'));
  const status = document.getElementById("example-status");

  const panelFor = (tab) => document.getElementById(tab.getAttribute("aria-controls"));

  const selectTab = (selectedTab, moveFocus = true, announce = true) => {
    tabs.forEach((tab) => {
      const isSelected = tab === selectedTab;
      const panel = panelFor(tab);

      tab.setAttribute("aria-selected", String(isSelected));
      tab.tabIndex = isSelected ? 0 : -1;
      tab.classList.toggle("is-active", isSelected);

      if (panel) {
        panel.hidden = !isSelected;
      }
    });

    if (moveFocus) {
      selectedTab.focus();
    }

    if (announce && status) {
      status.textContent = `${selectedTab.textContent.trim()} example selected.`;
    }
  };

  tabs.forEach((tab, index) => {
    tab.addEventListener("click", () => selectTab(tab, false));

    tab.addEventListener("keydown", (event) => {
      let nextIndex = index;

      switch (event.key) {
        case "ArrowLeft":
          nextIndex = (index - 1 + tabs.length) % tabs.length;
          break;
        case "ArrowRight":
          nextIndex = (index + 1) % tabs.length;
          break;
        case "Home":
          nextIndex = 0;
          break;
        case "End":
          nextIndex = tabs.length - 1;
          break;
        default:
          return;
      }

      event.preventDefault();
      selectTab(tabs[nextIndex]);
    });
  });

  const initiallySelected = tabs.find((tab) => tab.getAttribute("aria-selected") === "true") || tabs[0];

  if (initiallySelected) {
    selectTab(initiallySelected, false, false);
  }
})();
