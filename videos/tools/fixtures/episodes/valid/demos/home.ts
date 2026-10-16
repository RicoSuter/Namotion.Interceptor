import type {Demo} from '../../../../capture/config';

const demo: Demo = async (page, {baseUrl}) => {
  await page.goto(baseUrl);
};

export default demo;
